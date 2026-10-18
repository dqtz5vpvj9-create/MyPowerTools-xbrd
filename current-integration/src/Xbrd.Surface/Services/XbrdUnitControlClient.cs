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
    private readonly string _dataRoot;

    public XbrdUnitControlClient(TimeSpan timeout) : this(timeout, null, null) { }

    internal XbrdUnitControlClient(TimeSpan timeout, HttpMessageHandler? handler, string? dataRoot)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(15) : timeout;
        var configuredRoot = Environment.GetEnvironmentVariable("MPT_TOOL_DATA_ROOT");
        var platformRoot = Environment.GetEnvironmentVariable("MPT_DATA_ROOT");
        if (string.IsNullOrWhiteSpace(platformRoot))
        {
            platformRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyPowerTools");
        }
        _dataRoot = dataRoot ?? (!string.IsNullOrWhiteSpace(configuredRoot)
            ? configuredRoot.Trim()
            : Path.Combine(platformRoot.Trim(), "state", "tools", "xbrd"));
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

        cancellationToken.ThrowIfCancellationRequested();
        // In degraded mode this port belongs to another process. Consult the unit's own
        // diagnostic heartbeat first; do not POST to the process that won the port race.
        var triggerResult = await TryQueueTriggerAsync(sourceId, cancellationToken).ConfigureAwait(false);
        if (triggerResult is not null)
        {
            return triggerResult;
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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

    private async Task<XbrdPublishNowResult?> TryQueueTriggerAsync(string sourceId, CancellationToken cancellationToken)
    {
        var unit = sourceId == MemSourceId ? "xbrd.mem.service" : "xbrd.codex-quota.service";
        var triggerName = sourceId == MemSourceId ? "xbrd.mem.trigger" : "xbrd.codex-quota.trigger";
        var heartbeatPath = Path.Combine(_dataRoot, unit + ".heartbeat");
        var triggerPath = Path.Combine(_dataRoot, triggerName);
        try
        {
            if (!File.Exists(heartbeatPath) || new FileInfo(heartbeatPath).Length > 64 * 1024)
            {
                return null;
            }
            var text = await File.ReadAllTextAsync(heartbeatPath, cancellationToken).ConfigureAwait(false);
            if (JsonNode.Parse(text) is not JsonObject heartbeat ||
                XbrdJson.ReadString(heartbeat, "unit") != unit ||
                XbrdJson.ReadString(heartbeat, "control") != "trigger-file")
            {
                return null;
            }

            var interval = XbrdJson.ReadLong(heartbeat, "intervalSeconds", 300);
            // Manual triggering requires reasonably recent liveness even when a user chose a
            // very long publish interval. Do not accept a corrupt 68-year freshness window.
            var allowedAge = TimeSpan.FromSeconds(Math.Clamp(interval, 5, 86_400) + 30d);
            if (!DateTimeOffset.TryParse(XbrdJson.ReadString(heartbeat, "observedAt"),
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var observed) ||
                observed > DateTimeOffset.UtcNow.AddSeconds(30) || DateTimeOffset.UtcNow - observed > allowedAge)
            {
                return new XbrdPublishNowResult(false, sourceId, "", 0, "unreachable",
                    "unit 的触发文件心跳已过期或无效，请先确认服务正在运行。", heartbeatPath);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var previous = File.GetLastWriteTimeUtc(triggerPath);
            // Keep mtime increasing even for rapid repeated requests / coarse timestamp precision.
            var modified = DateTime.UtcNow;
            if (modified <= previous.AddSeconds(1))
            {
                modified = previous.AddSeconds(1);
            }
            using (var stream = new FileStream(triggerPath, FileMode.OpenOrCreate, FileAccess.Write,
                       FileShare.Read, 4096, FileOptions.WriteThrough))
            {
                stream.SetLength(0);
                var bytes = System.Text.Encoding.UTF8.GetBytes(modified.ToString("O", CultureInfo.InvariantCulture));
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
                File.SetLastWriteTimeUtc(stream.SafeFileHandle, modified);
                stream.Flush(flushToDisk: true);
            }

            // A durable request is not proof of publication. The unit's next heartbeat reports
            // the actual outcome (including disabled); never label this result as published.
            return new XbrdPublishNowResult(true, sourceId, "", 0, "trigger-queued", "", triggerPath, true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return new XbrdPublishNowResult(false, sourceId, "", 0, "unreachable",
                $"无法读取 unit 心跳或写入触发文件：{ex.Message}", triggerPath);
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
