using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Xbrd.Surface.Services;

/// <summary>
/// Talks to the local Service Unit control endpoints (CONTRACT §5 / task-8 C2):
/// <c>POST http://127.0.0.1:19225/publish-now</c> (mem) and <c>…:19226/publish-now</c> (codex-quota).
/// This is what makes 「立即发布」 a real action for the two sources this tool produces, without
/// restarting the unit process.
/// </summary>
public sealed class XbrdUnitControlClient : IDisposable
{
    public const int DefaultMemPort = 19225;
    public const int DefaultCodexPort = 19226;
    public const string MemSourceId = "quota.mem";
    public const string CodexSourceId = "quota.codex";

    private readonly HttpClient _http;

    public XbrdUnitControlClient(TimeSpan timeout)
    {
        _http = new HttpClient
        {
            Timeout = timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(15) : timeout
        };
    }

    /// <summary>
    /// Loopback control port for a source this tool produces, or null for any other source.
    /// Ports are frozen by CONTRACT §5 (mem 19225 / codex-quota 19226). The two environment
    /// overrides are Surface-side only — the unit reads its own <c>XBRD_CONTROL_PORT</c>, which the
    /// Shell cannot see, so these exist for probes and unusual deployments.
    /// </summary>
    public static int? PortForSource(string sourceId) => sourceId switch
    {
        MemSourceId => ReadPort("XBRD_MEM_CONTROL_PORT", DefaultMemPort),
        CodexSourceId => ReadPort("XBRD_CODEX_CONTROL_PORT", DefaultCodexPort),
        _ => null
    };

    public static string EndpointForSource(string sourceId)
    {
        var port = PortForSource(sourceId);
        return port is null ? "" : $"http://127.0.0.1:{port}/publish-now";
    }

    public async Task<XbrdPublishNowResult> PublishNowAsync(string sourceId, CancellationToken cancellationToken)
    {
        var port = PortForSource(sourceId);
        if (port is null)
        {
            return new XbrdPublishNowResult(false, sourceId, "", 0, "", $"{sourceId} 不由本工具的 Service Unit 生产。", "");
        }

        var endpoint = $"http://127.0.0.1:{port}/publish-now";
        try
        {
            using var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(endpoint, content, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;

            if (status == 404)
            {
                return new XbrdPublishNowResult(false, sourceId, "", 0, "", $"unit 控制端点 404（{endpoint}）：unit 版本过旧，缺少 /publish-now。", endpoint);
            }

            JsonObject? root = null;
            try
            {
                root = JsonNode.Parse(body) as JsonObject;
            }
            catch (JsonException)
            {
                root = null;
            }

            if (root is null)
            {
                return new XbrdPublishNowResult(false, sourceId, "", 0, "", $"unit 控制端点响应不是 JSON（HTTP {status}）。", endpoint);
            }

            var ok = XbrdJson.ReadBool(root, "ok", status is >= 200 and < 300);
            if (!ok)
            {
                var error = XbrdJson.ReadString(root, "error");
                return new XbrdPublishNowResult(
                    false,
                    sourceId,
                    XbrdJson.ReadString(root, "revision"),
                    XbrdJson.ReadLong(root, "duration_ms"),
                    XbrdJson.ReadString(root, "status"),
                    error.Length > 0 ? error : $"unit 报告 ok=false（HTTP {status}）。",
                    endpoint);
            }

            return new XbrdPublishNowResult(
                true,
                sourceId,
                XbrdJson.ReadString(root, "revision"),
                XbrdJson.ReadLong(root, "duration_ms"),
                XbrdJson.ReadString(root, "status", "published"),
                "",
                endpoint,
                XbrdJson.ReadBool(root, "skipped"));
        }
        catch (TaskCanceledException)
        {
            return new XbrdPublishNowResult(false, sourceId, "", 0, "", $"unit 控制端点超时（{endpoint}）。", endpoint);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or UriFormatException)
        {
            return new XbrdPublishNowResult(
                false,
                sourceId,
                "",
                0,
                "",
                string.Create(CultureInfo.InvariantCulture, $"unit 控制端点不可达（{endpoint}）：{ex.Message}。需要该 unit 正在运行且已支持 /publish-now（task-8/C2）。"),
                endpoint);
        }
    }

    private static int ReadPort(string environmentVariable, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(environmentVariable);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && port is > 0 and < 65536
            ? port
            : fallback;
    }

    public void Dispose() => _http.Dispose();
}
