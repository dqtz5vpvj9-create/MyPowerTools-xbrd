using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MyPowerTools.Platform.Abstractions;

namespace Xbrd.CodexQuota.Service;

/// <summary>
/// xbrd.codex-quota.service 骨架实现（t0）。
///
/// 已证明：跨仓引用 MyPowerTools.Platform.Abstractions 可编译，CodexQuotaReader 可调用。
/// 待完成（所有者 C）：
///   1. 按 esp32-screen/tools/xbrd_codex_quota_plugin.py 的 panel_patch 形状发布
///      quota.codex（live changed_fields 为 quota.codex.h5_left / h5_reset / d7_left / d7_reset），
///      POST {publisher}/api/v1/sources/quota.codex/snapshot，ttl_s=300。
///   2. 失败时发布 degraded 快照而不覆盖 last-good，并写心跳文件。
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var intervalSeconds = int.TryParse(
            Environment.GetEnvironmentVariable("XBRD_CODEX_INTERVAL_SECONDS"),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var parsed) && parsed > 0
            ? parsed
            : 300;
        var dataRoot = Environment.GetEnvironmentVariable("MPT_TOOL_DATA_ROOT")
                       ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyPowerTools", "state", "tools", "xbrd");
        Directory.CreateDirectory(dataRoot);
        var heartbeatFile = Path.Combine(dataRoot, "xbrd.codex-quota.service.heartbeat");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

        Console.WriteLine($"[xbrd.codex] started interval={intervalSeconds}s dataRoot={dataRoot}");

        while (!cts.IsCancellationRequested)
        {
            try
            {
                var snapshot = await CodexQuotaReader.ReadAsync(cts.Token);
                var payload = new JsonObject
                {
                    ["source"] = snapshot.Source,
                    ["displayRemainingPercent"] = snapshot.DisplayWindow?.RemainingPercent,
                    ["displayResetsAt"] = snapshot.DisplayWindow?.ResetsAt?.ToString("O", CultureInfo.InvariantCulture),
                    ["shortRemainingPercent"] = snapshot.ShortWindow?.RemainingPercent,
                    ["weeklyRemainingPercent"] = snapshot.WeeklyWindow?.RemainingPercent,
                    ["observedAt"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
                };
                Console.WriteLine($"[xbrd.codex] quota read: {payload.ToJsonString(new JsonSerializerOptions { WriteIndented = false })}");
                await File.WriteAllTextAsync(heartbeatFile, payload.ToJsonString(), cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[xbrd.codex] quota read failed: {ex.Message}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, intervalSeconds)), cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        Console.WriteLine("[xbrd.codex] stopped");
        return 0;
    }
}
