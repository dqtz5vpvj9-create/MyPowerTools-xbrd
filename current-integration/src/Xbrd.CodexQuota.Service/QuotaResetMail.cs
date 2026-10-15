using System.Diagnostics;
using System.Text.Json;
using MyPowerTools.Platform.Abstractions;

namespace Xbrd.CodexQuota.Service;

// Configuration and delivery state stay in the user's tool data directory.
internal sealed class QuotaResetMail(string dataRoot)
{
    internal sealed record Config(bool Enabled, string Recipient, string CliPath);
    internal sealed record Notice(string Subject, string Body);
    internal sealed record State(CodexQuotaSnapshot? Previous, DateTimeOffset ObservedAt, List<Notice> Pending);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal static List<string> Detect(CodexQuotaSnapshot previous, CodexQuotaSnapshot current,
        DateTimeOffset now)
    {
        var changes = new List<string>();
        Check("短周期额度", previous.ShortWindow, current.ShortWindow);
        Check("每周额度", previous.WeeklyWindow, current.WeeklyWindow);
        return changes;

        void Check(string name, CodexQuotaWindow? before, CodexQuotaWindow? after)
        {
            if (before is null || after is null || before.WindowMinutes != after.WindowMinutes ||
                !double.IsFinite(before.UsedPercent) || !double.IsFinite(after.UsedPercent) ||
                before.UsedPercent is < 0 or > 100 || after.UsedPercent is < 0 or > 100)
                return;
            // A renewed window proves a scheduled reset even if usage already exceeded the old value.
            var renewed = before.ResetsAt is { } oldReset && after.ResetsAt is { } newReset &&
                oldReset <= now && newReset > now && newReset > oldReset;
            var recovered = after.UsedPercent < before.UsedPercent;
            if (!renewed && !recovered) return;
            var reason = renewed ? "周期重置" : "检测到额度恢复（提前重置或额度调整）";
            changes.Add($"{name}：{reason}；剩余 {before.RemainingPercent}% → {after.RemainingPercent}%。");
        }
    }

    internal async Task ObserveAsync(CodexQuotaSnapshot current, DateTimeOffset now, CancellationToken token)
    {
        var configPath = Path.Combine(dataRoot, "codex-reset-mail.json");
        if (!File.Exists(configPath)) return;
        var config = JsonSerializer.Deserialize<Config>(await File.ReadAllTextAsync(configPath, token));
        if (config is not { Enabled: true } || string.IsNullOrWhiteSpace(config.Recipient) ||
            string.IsNullOrWhiteSpace(config.CliPath)) return;
        var statePath = Path.Combine(dataRoot, "codex-reset-mail-state.json");
        var state = File.Exists(statePath)
            ? JsonSerializer.Deserialize<State>(await File.ReadAllTextAsync(statePath, token))
                ?? throw new InvalidDataException("Invalid reset mail state")
            : new State(null, now, []);
        if (now <= state.ObservedAt && state.Previous is not null) return;
        var changes = state.Previous is null ? [] : Detect(state.Previous, current, now);
        if (changes.Count > 0)
        {
            var chinaTime = now.ToOffset(TimeSpan.FromHours(8));
            state.Pending.Add(new Notice("Codex 额度重置提醒",
                $"检测时间：{chinaTime:yyyy-MM-dd HH:mm:ss}（北京时间）\n\n" +
                string.Join("\n", changes) + "\n\n额度数据来自本机 Codex 额度监视。"));
        }
        // Persist the new baseline and outbox together before attempting delivery.
        state = state with { Previous = current, ObservedAt = now };
        await SaveAsync(statePath, state, token);
        while (state.Pending.Count > 0)
        {
            await SendAsync(config, state.Pending[0], token);
            state.Pending.RemoveAt(0);
            await SaveAsync(statePath, state, token);
        }
    }

    private static async Task SaveAsync(string path, State state, CancellationToken token)
    {
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(state, JsonOptions), token);
        File.Move(temporary, path, overwrite: true);
    }

    private static async Task SendAsync(Config config, Notice notice, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var start = new ProcessStartInfo(config.CliPath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        // Automatic mail delivery is explicitly enabled by the user's local configuration.
        foreach (var argument in new[] { "message", "+send", "--to", config.Recipient,
                     "--subject", notice.Subject, "--body", notice.Body, "--confirmed" })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Cannot start mail CLI");
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var output = await stdout;
            await stderr;
            if (process.ExitCode != 0) throw new IOException($"Mail CLI exit code {process.ExitCode}");
            using var response = JsonDocument.Parse(output);
            if (!response.RootElement.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
                throw new IOException("Mail CLI did not confirm delivery");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("Mail CLI timed out");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }
}
