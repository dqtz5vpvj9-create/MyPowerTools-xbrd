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
    /// Reads the panel document. The router has no HTML page (<c>/panel</c> serves the same JSON),
    /// so this is the tool's only view of the panel's real content.
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
                    XbrdJson.ReadString(node, "error")));
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
