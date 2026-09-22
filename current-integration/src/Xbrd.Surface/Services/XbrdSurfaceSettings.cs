using System.Globalization;
using System.Text.Json.Nodes;
using MyPowerTools.HostControl;

namespace Xbrd.Surface.Services;

/// <summary>
/// Effective tool settings for this Surface. Values come from the tool's own
/// <c>ui/settings.json</c> (the same file the Shell uses to expand <c>${settings.*}</c> in
/// <c>tool.json</c>), reached through the Runner's tool descriptor. When the host is unreachable the
/// contract defaults from CONTRACT.md §3 are used and the Surface says so instead of failing.
/// Secret values are never read, copied or logged.
/// </summary>
public sealed record XbrdSurfaceSettings(
    string PublisherUrl,
    string PanelUrl,
    string XbrdRepoRoot,
    int ConnectionTimeoutMs,
    bool AutoRefresh,
    bool EnableMemSource,
    int MemIntervalSeconds,
    bool EnableCodexSource,
    int CodexIntervalSeconds,
    string Origin,
    string ValuesPath,
    IReadOnlyList<string> DeclaredSecrets,
    string FallbackReason = "")
{
    public const string ContractDefaultPublisherUrl = "http://ow.lixinrui000.cn:8080";
    public const string ContractDefaultPanelUrl = "http://ow.lixinrui000.cn:8080/panel";
    public const string ContractDefaultRepoRoot = @"C:\Users\lixinrui\repo\esp32-screen";

    public static XbrdSurfaceSettings Fallback(string reason) => new(
        ContractDefaultPublisherUrl,
        ContractDefaultPanelUrl,
        ContractDefaultRepoRoot,
        5000,
        true,
        true,
        300,
        true,
        300,
        "contract-default",
        "",
        [],
        reason);

    public bool UsesContractDefaults => Origin == "contract-default";

    public TimeSpan Timeout =>
        TimeSpan.FromMilliseconds(Math.Clamp(ConnectionTimeoutMs, 100, 60_000));

    public string OriginText => Origin switch
    {
        "tool-settings" => "工具设置（ui/settings.json）",
        "host-settings-store" => "宿主设置存储",
        _ => "契约默认值"
    };

    public string DiagnosticsText => Origin switch
    {
        "tool-settings" => $"设置来源：工具设置 {ValuesPath}",
        "host-settings-store" => "设置来源：宿主设置存储（工具 values 文件不可读）",
        _ => FallbackReason.Length > 0
            ? $"设置来源：契约默认值（{FallbackReason}）"
            : "设置来源：契约默认值"
    };

    public string RepoRootText => string.IsNullOrWhiteSpace(XbrdRepoRoot) ? "(未配置)" : XbrdRepoRoot;

    public string SecretsText => DeclaredSecrets.Count == 0
        ? "无"
        : string.Join(", ", DeclaredSecrets) + "（本 Surface 不读取密钥值）";

    public string ScriptPath(string fileName) =>
        string.IsNullOrWhiteSpace(XbrdRepoRoot)
            ? fileName
            : Path.Combine(XbrdRepoRoot, "scripts", fileName);
}

public static class XbrdSettingsLoader
{
    public const string ToolId = "xbrd";

    /// <summary>
    /// Resolves the tool descriptor through HostControl, then reads the declared values file.
    /// Never throws: every failure path degrades to <see cref="XbrdSurfaceSettings.Fallback"/>.
    /// The HostControl call lives in <see cref="LoadCoreAsync"/> on purpose — a missing
    /// HostControl assembly fails while the callee is JIT-compiled, which only the caller's
    /// try/catch (not the callee's own) can absorb.
    /// </summary>
    public static async Task<XbrdSurfaceSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return XbrdSurfaceSettings.Fallback(ex.Message);
        }
    }

    private static async Task<XbrdSurfaceSettings> LoadCoreAsync(CancellationToken cancellationToken)
    {
        using var client = HostControlClient.ForDefaultEndpoint();
        var descriptor = await client.GetToolAsync(ToolId, cancellationToken).ConfigureAwait(false);
        var valuesPath = descriptor.Settings?.ValuesPath ?? "";
        IReadOnlyList<string> secrets = descriptor.Settings is null
            ? Array.Empty<string>()
            : descriptor.Settings.Secrets.Where(static name => !string.IsNullOrWhiteSpace(name)).ToArray();

        if (!string.IsNullOrWhiteSpace(valuesPath) && File.Exists(valuesPath))
        {
            var text = await File.ReadAllTextAsync(valuesPath, cancellationToken).ConfigureAwait(false);
            if (JsonNode.Parse(text) is JsonObject values && values.Count > 0)
            {
                return Parse(values, "tool-settings", valuesPath, secrets);
            }
        }

        var snapshot = await client.GetSettingsAsync(ToolId, cancellationToken).ConfigureAwait(false);
        var stored = JsonStructMapper.ToJsonObject(snapshot.Values);
        if (stored.Count > 0)
        {
            return Parse(stored, "host-settings-store", valuesPath, secrets);
        }

        return XbrdSurfaceSettings.Fallback("host descriptor declared no settings values");
    }

    internal static XbrdSurfaceSettings Parse(
        JsonObject values,
        string origin,
        string valuesPath,
        IReadOnlyList<string> secrets)
    {
        var publisherUrl = ReadString(values, "publisherUrl", XbrdSurfaceSettings.ContractDefaultPublisherUrl);
        var panelUrl = ReadString(values, "panelUrl", XbrdSurfaceSettings.ContractDefaultPanelUrl);
        var repoRoot = ReadString(values, "xbrdRepoRoot", XbrdSurfaceSettings.ContractDefaultRepoRoot);
        var timeout = ReadInt(values, "connectionTimeoutMs", 5000);
        var memInterval = ReadInt(values, "memIntervalSeconds", 300);
        var codexInterval = ReadInt(values, "codexIntervalSeconds", 300);

        return new XbrdSurfaceSettings(
            publisherUrl.Length == 0 ? XbrdSurfaceSettings.ContractDefaultPublisherUrl : publisherUrl,
            panelUrl.Length == 0 ? XbrdSurfaceSettings.ContractDefaultPanelUrl : panelUrl,
            repoRoot.Length == 0 ? XbrdSurfaceSettings.ContractDefaultRepoRoot : repoRoot,
            Math.Clamp(timeout, 100, 60_000),
            ReadBool(values, "autoRefresh", true),
            ReadBool(values, "enableMemSource", true),
            Math.Max(5, memInterval),
            ReadBool(values, "enableCodexSource", true),
            Math.Max(5, codexInterval),
            origin,
            valuesPath,
            secrets);
    }

    private static string ReadString(JsonObject values, string key, string fallback)
    {
        try
        {
            return values[key]?.GetValue<string>()?.Trim() ?? fallback;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return fallback;
        }
    }

    private static int ReadInt(JsonObject values, string key, int fallback)
    {
        try
        {
            var node = values[key];
            if (node is null)
            {
                return fallback;
            }

            return node.GetValueKind() == System.Text.Json.JsonValueKind.Number
                ? node.GetValue<int>()
                : int.TryParse(node.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : fallback;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or OverflowException)
        {
            return fallback;
        }
    }

    private static bool ReadBool(JsonObject values, string key, bool fallback)
    {
        try
        {
            var node = values[key];
            if (node is null)
            {
                return fallback;
            }

            return node.GetValueKind() is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False
                ? node.GetValue<bool>()
                : bool.TryParse(node.ToString(), out var parsed) ? parsed : fallback;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return fallback;
        }
    }
}
