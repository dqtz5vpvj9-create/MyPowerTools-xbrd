using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Xbrd.Surface.Services;

/// <summary>
/// Read-only client for the router publisher (CONTRACT.md §6). The Surface never writes to the
/// publisher: <c>GET /health</c> answers panel reachability and <c>GET /api/v1/sources</c> answers
/// the source table. Publishing stays with the Service Units (owner C).
/// </summary>
public sealed class XbrdPublisherClient : IDisposable
{
    private readonly HttpClient _http;

    public XbrdPublisherClient(TimeSpan timeout, HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(5) : timeout;
    }

    public async Task<XbrdHealthSnapshot> GetHealthAsync(string publisherUrl, CancellationToken cancellationToken)
    {
        var endpoint = BuildEndpoint(publisherUrl, "/health");
        try
        {
            var (body, _) = await GetStringAsync(endpoint, cancellationToken);
            return ParseHealth(body, endpoint);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException or JsonException)
        {
            return XbrdHealthSnapshot.Failed(endpoint, Describe(ex, cancellationToken));
        }
    }

    public async Task<XbrdSourcesSnapshot> GetSourcesAsync(string publisherUrl, CancellationToken cancellationToken)
    {
        var endpoint = BuildEndpoint(publisherUrl, "/api/v1/sources");
        try
        {
            var (body, _) = await GetStringAsync(endpoint, cancellationToken);
            return ParseSources(body, endpoint, "direct-http");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException or JsonException)
        {
            return XbrdSourcesSnapshot.Failed(endpoint, Describe(ex, cancellationToken), "direct-http", DateTimeOffset.Now);
        }
    }

    /// <summary>
    /// Reads the raw panel document: 原始 panel JSON（设备与发布器的原始数据入口）；可读 UI 见
    /// <c>/panel-app/</c>（复用手机端配额 UI）。
    /// </summary>
    public async Task<XbrdPanelSnapshot> GetPanelAsync(string publisherUrl, CancellationToken cancellationToken)
    {
        var endpoint = BuildEndpoint(publisherUrl, "/panel.json");
        try
        {
            var (body, _) = await GetStringAsync(endpoint, cancellationToken);
            return XbrdPanelParser.Parse(body, endpoint);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException or JsonException)
        {
            return XbrdPanelSnapshot.Failed(endpoint, Describe(ex, cancellationToken));
        }
    }

    // ---------------------------------------------------------------- control plane (CONTRACT §6)

    /// <summary>
    /// <c>POST /api/v1/sources/&lt;id&gt;/refresh</c> — the publisher really runs that source's plugin
    /// (the same thing its cron does). Returns exit code / duration / stdout tail as evidence.
    /// </summary>
    public Task<XbrdControlResult> RefreshSourceAsync(string publisherUrl, string sourceId, CancellationToken cancellationToken) =>
        SendControlAsync(HttpMethod.Post, publisherUrl, $"/api/v1/sources/{Uri.EscapeDataString(sourceId)}/refresh", "refresh", sourceId, cancellationToken);

    /// <summary><c>POST .../disable</c> or <c>.../enable</c>: the publisher stops/restarts accounting for the source.</summary>
    public Task<XbrdControlResult> SetSourceEnabledAsync(string publisherUrl, string sourceId, bool enabled, CancellationToken cancellationToken) =>
        SendControlAsync(
            HttpMethod.Post,
            publisherUrl,
            $"/api/v1/sources/{Uri.EscapeDataString(sourceId)}/{(enabled ? "enable" : "disable")}",
            enabled ? "enable" : "disable",
            sourceId,
            cancellationToken);

    /// <summary><c>DELETE /api/v1/sources/&lt;id&gt;</c> — unregister a source with no producer left.</summary>
    public Task<XbrdControlResult> DeleteSourceAsync(string publisherUrl, string sourceId, CancellationToken cancellationToken) =>
        SendControlAsync(HttpMethod.Delete, publisherUrl, $"/api/v1/sources/{Uri.EscapeDataString(sourceId)}", "delete", sourceId, cancellationToken);

    /// <summary><c>GET /api/v1/sources/&lt;id&gt;/log?lines=N</c> — real log lines, whitelisted server-side.</summary>
    public async Task<XbrdSourceLogResult> GetSourceLogAsync(string publisherUrl, string sourceId, int lines, CancellationToken cancellationToken)
    {
        var endpoint = BuildEndpoint(publisherUrl, $"/api/v1/sources/{Uri.EscapeDataString(sourceId)}/log?lines={Math.Clamp(lines, 1, 1000)}");
        try
        {
            var (body, status) = await SendAsync(HttpMethod.Get, endpoint, cancellationToken).ConfigureAwait(false);
            if (status == 404)
            {
                return XbrdSourceLogResult.Failed(sourceId, $"路由器端点 404：该来源没有日志能力（{endpoint}）", status);
            }

            var root = ParseObject(body);
            if (root is null)
            {
                return XbrdSourceLogResult.Failed(sourceId, $"日志响应不是 JSON（HTTP {status}）。", status);
            }

            if (!XbrdJson.ReadBool(root, "ok", true))
            {
                var error = XbrdJson.ReadString(root, "error");
                return XbrdSourceLogResult.Failed(sourceId, error.Length > 0 ? error : "发布器报告 ok=false。", status);
            }

            var logLines = new List<string>();
            if (root["lines"] is JsonArray array)
            {
                foreach (var item in array)
                {
                    var line = item is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : item?.ToJsonString();
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        logLines.Add(line!);
                    }
                }
            }

            return new XbrdSourceLogResult(
                true,
                sourceId,
                XbrdJson.ReadString(root, "log_path"),
                logLines,
                XbrdJson.ReadBool(root, "truncated"),
                "",
                status);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException or JsonException)
        {
            return XbrdSourceLogResult.Failed(sourceId, Describe(ex, cancellationToken), 0);
        }
    }

    private async Task<XbrdControlResult> SendControlAsync(
        HttpMethod method,
        string publisherUrl,
        string path,
        string action,
        string sourceId,
        CancellationToken cancellationToken)
    {
        var endpoint = BuildEndpoint(publisherUrl, path);
        var startedAt = DateTimeOffset.Now;
        try
        {
            var (body, status) = await SendAsync(method, endpoint, cancellationToken).ConfigureAwait(false);
            var elapsed = (long)(DateTimeOffset.Now - startedAt).TotalMilliseconds;
            if (status is 404 or 405)
            {
                return new XbrdControlResult(
                    action,
                    sourceId,
                    XbrdControlStatus.Unsupported,
                    status,
                    $"路由器端点 {status}：这是旧发布器，不支持 {action}（{endpoint}）",
                    "",
                    elapsed,
                    null,
                    null,
                    "",
                    Truncate(body));
            }

            var root = ParseObject(body);
            if (root is null)
            {
                return new XbrdControlResult(
                    action,
                    sourceId,
                    status is >= 200 and < 300 ? XbrdControlStatus.Failed : XbrdControlStatus.Failed,
                    status,
                    $"响应不是 JSON（HTTP {status}）。",
                    Truncate(body),
                    elapsed,
                    null,
                    null,
                    "",
                    Truncate(body));
            }

            var ok = XbrdJson.ReadBool(root, "ok", status is >= 200 and < 300);
            var error = XbrdJson.ReadString(root, "error");
            var durationMs = XbrdJson.ReadLong(root, "duration_ms", elapsed);
            int? exitCode = root["exit_code"] is null ? null : XbrdJson.ReadInt(root, "exit_code");
            bool? snapshotUpdated = root["snapshot_updated"] is null ? null : XbrdJson.ReadBool(root, "snapshot_updated");
            var stdoutTail = XbrdJson.ReadString(root, "stdout_tail");

            var message = ok
                ? $"{action} 完成"
                : error.Length > 0
                    ? error
                    : $"发布器返回 ok=false（HTTP {status}）。";

            return new XbrdControlResult(
                action,
                sourceId,
                ok ? XbrdControlStatus.Ok : XbrdControlStatus.Failed,
                status,
                message,
                $"HTTP {status}",
                durationMs,
                exitCode,
                snapshotUpdated,
                stdoutTail,
                Truncate(body));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException)
        {
            var elapsed = (long)(DateTimeOffset.Now - startedAt).TotalMilliseconds;
            return new XbrdControlResult(
                action,
                sourceId,
                XbrdControlStatus.Unreachable,
                0,
                $"路由器不可达：{Describe(ex, cancellationToken)}",
                endpoint,
                elapsed,
                null,
                null,
                "",
                "");
        }
    }

    private static JsonObject? ParseObject(string body)
    {
        try
        {
            return JsonNode.Parse(body) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Truncate(string text, int max = 4000) =>
        text.Length <= max ? text : text[..max] + "…";

    private async Task<(string Body, int Status)> SendAsync(HttpMethod method, string endpoint, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, endpoint);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return (body, (int)response.StatusCode);
    }

    public static string BuildEndpoint(string publisherUrl, string path)
    {
        var root = (publisherUrl ?? "").Trim();
        if (root.Length == 0)
        {
            return path;
        }

        return root.TrimEnd('/') + "/" + path.TrimStart('/');
    }

    public static XbrdSourcesSnapshot ParseSources(string body, string endpoint, string transport)
    {
        var fetchedAt = DateTimeOffset.Now;
        JsonObject? root;
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
            return XbrdSourcesSnapshot.Failed(endpoint, "发布器返回的不是 JSON 对象。", transport, fetchedAt);
        }

        var ok = XbrdJson.ReadBool(root, "ok", true);
        var schema = XbrdJson.ReadString(root, "schema");
        if (!ok)
        {
            var reason = XbrdJson.ReadString(root, "error");
            return XbrdSourcesSnapshot.Failed(
                endpoint,
                reason.Length > 0 ? reason : "发布器报告 ok=false。",
                transport,
                fetchedAt);
        }

        var sources = new List<XbrdSourceSummary>();
        if (root["sources"] is JsonArray array)
        {
            foreach (var item in array)
            {
                var node = XbrdJson.AsObject(item);
                if (node is null)
                {
                    continue;
                }

                var sourceId = XbrdJson.ReadString(node, "source_id");
                if (sourceId.Length == 0)
                {
                    continue;
                }

                var capabilities = ReadCapabilities(node["capabilities"]);
                sources.Add(new XbrdSourceSummary(
                    sourceId,
                    XbrdJson.ReadString(node, "kind"),
                    XbrdJson.ReadString(node, "status", "unknown"),
                    XbrdJson.ReadString(node, "effective_status", "unknown"),
                    XbrdJson.ReadBool(node, "expired"),
                    XbrdJson.ReadLong(node, "age_s"),
                    XbrdJson.ReadLong(node, "ttl_s"),
                    XbrdJson.ReadTime(node, "generated_at"),
                    XbrdJson.ReadTime(node, "received_at"),
                    XbrdJson.ReadString(node, "revision"),
                    XbrdJson.ReadStringArray(node, "changed_fields"),
                    XbrdJson.ReadString(node, "error"),
                    XbrdJson.ReadBool(node, "enabled", true),
                    capabilities.Names,
                    capabilities.Declared));
            }
        }

        sources.Sort(static (left, right) => string.CompareOrdinal(left.SourceId, right.SourceId));

        return new XbrdSourcesSnapshot(
            true,
            schema,
            XbrdJson.ReadInt(root, "schema_version"),
            XbrdJson.ReadInt(root, "count", sources.Count),
            XbrdJson.ReadInt(root, "expired_count", sources.Count(static source => source.Expired)),
            XbrdJson.ReadInt(root, "unhealthy_count", sources.Count(static source => source.Severity != XbrdSeverity.Ready)),
            sources,
            fetchedAt,
            endpoint,
            "",
            transport);
    }

    public static XbrdHealthSnapshot ParseHealth(string body, string endpoint)
    {
        JsonObject? root;
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
            return XbrdHealthSnapshot.Failed(endpoint, "发布器 /health 返回的不是 JSON 对象。");
        }

        var ok = XbrdJson.ReadBool(root, "ok", true);
        if (!ok)
        {
            return XbrdHealthSnapshot.Failed(endpoint, "发布器 /health 报告 ok=false。");
        }

        return new XbrdHealthSnapshot(
            true,
            XbrdJson.ReadBool(root, "panel_ok", true),
            XbrdJson.ReadString(root, "service"),
            XbrdJson.ReadString(root, "host"),
            XbrdJson.ReadTime(root, "time"),
            XbrdJson.ReadLong(root, "uptime_seconds"),
            XbrdJson.ReadInt(root, "source_count"),
            XbrdJson.ReadInt(root, "source_expired_count"),
            XbrdJson.ReadInt(root, "source_unhealthy_count"),
            XbrdJson.ReadString(root, "panel_source"),
            XbrdJson.ReadString(root, "panel_updated"),
            XbrdJson.ReadStringArray(root, "panel_errors"),
            XbrdJson.ReadStringArray(root, "panel_warnings"),
            endpoint,
            "");
    }

    private static (IReadOnlyList<string> Names, bool Declared) ReadCapabilities(JsonNode? node)
    {
        switch (node)
        {
            case JsonArray array:
            {
                var names = array
                    .Select(item => item is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null)
                    .Where(static name => !string.IsNullOrWhiteSpace(name))
                    .Select(static name => name!)
                    .ToArray();
                return (names, true);
            }

            case JsonObject map:
            {
                var names = map
                    .Where(pair => pair.Value is JsonValue value && value.GetValueKind() is JsonValueKind.True or JsonValueKind.False && value.GetValue<bool>())
                    .Select(static pair => pair.Key)
                    .ToArray();
                return (names, true);
            }

            default:
                return (Array.Empty<string>(), false);
        }
    }

    private async Task<(string Body, int Status)> GetStringAsync(string endpoint, CancellationToken cancellationToken)
    {
        using var response = await _http
            .GetAsync(endpoint, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".Trim());
        }

        return (body, (int)response.StatusCode);
    }

    private static string Describe(Exception exception, CancellationToken cancellationToken) =>
        exception is TaskCanceledException && !cancellationToken.IsCancellationRequested
            ? "请求超时（connectionTimeoutMs）。"
            : exception.Message;

    public void Dispose() => _http.Dispose();
}
