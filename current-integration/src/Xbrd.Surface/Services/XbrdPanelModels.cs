using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Xbrd.Surface.Services;

/// <summary>One scalar label/value pair flattened out of a panel object (status / weather / plan).</summary>
public sealed record XbrdPanelField(string Label, string Value, string SeverityToken, XbrdSeverity Severity)
{
    public bool IsReady => Severity == XbrdSeverity.Ready;

    public bool IsDegraded => Severity == XbrdSeverity.Degraded;

    public bool IsError => Severity == XbrdSeverity.Error;

    public string Display => $"{Label} {Value}";
}

/// <summary>
/// Read-only projection of <c>GET {publisherUrl}/panel.json</c> (schema <c>xbrd.panel.v1</c>) —
/// 原始 panel JSON（设备与发布器的原始数据入口）；可读 UI 见 <c>/panel-app/</c>（复用手机端配额 UI）。
/// Only the sections the readable UI does not show are previewed here (status / weather / plan):
/// <c>quota</c> belongs to the panel Tab, so it is intentionally not repeated on this page.
/// </summary>
public sealed record XbrdPanelSnapshot(
    bool Ok,
    string Schema,
    int SchemaVersion,
    string Updated,
    IReadOnlyList<XbrdPanelField> StatusFields,
    IReadOnlyList<XbrdPanelField> WeatherFields,
    IReadOnlyList<XbrdPanelField> PlanFields,
    string TodosText,
    int OtherFieldCount,
    string Error,
    string Endpoint,
    DateTimeOffset FetchedAt)
{
    public static XbrdPanelSnapshot Empty { get; } = Failed("", "尚未读取。");

    public static XbrdPanelSnapshot Failed(string endpoint, string error) =>
        new(false, "", 0, "", [], [], [], "", 0, error, endpoint, DateTimeOffset.Now);

    /// <summary>Aggregate pill of the card: worst of <c>status.network/wifi/bluetooth/battery</c>.</summary>
    public XbrdSeverity StatusSeverity
    {
        get
        {
            var worst = XbrdSeverity.Ready;
            foreach (var entry in StatusFields)
            {
                worst = worst.Worst(entry.Severity);
            }

            return StatusFields.Count == 0 ? XbrdSeverity.Degraded : worst;
        }
    }

    public string PillToken => StatusSeverity.ToToken();

    public bool IsReady => StatusSeverity == XbrdSeverity.Ready;

    public bool IsDegraded => StatusSeverity == XbrdSeverity.Degraded;

    public bool IsError => StatusSeverity == XbrdSeverity.Error;

    public bool HasStatus => StatusFields.Count > 0;

    public bool HasWeather => WeatherFields.Count > 0;

    public bool HasPlan => PlanFields.Count > 0;

    public bool HasError => !Ok && Error.Length > 0;

    public string UpdatedText => Updated.Length == 0 ? "—" : Updated;

    public string SchemaText => Schema.Length == 0 ? "—" : $"{Schema} v{SchemaVersion}";

    public string CountsText =>
        $"status {StatusFields.Count} · weather {WeatherFields.Count} · plan {PlanFields.Count}" +
        (OtherFieldCount > 0 ? $" · 其它字段 {OtherFieldCount}" : "");

    public string ErrorText => Ok
        ? ""
        : $"{Error}（端点 {Endpoint}）";
}

/// <summary>
/// Tolerant, structure-driven reader for the panel document. Nothing here invents fields: absent
/// sections are reported as absent and every scalar the payload carries is shown.
/// </summary>
public static class XbrdPanelParser
{
    private const int MaxDepth = 3;

    public static XbrdPanelSnapshot Parse(string body, string endpoint)
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
            return XbrdPanelSnapshot.Failed(endpoint, "面板数据不是 JSON 对象。");
        }

        var schema = ReadScalar(root["schema"]);
        var version = ReadInt(root["schema_version"]);

        var statusFields = Flatten(root["status"] as JsonObject, "status", classify: true);
        var weatherFields = Flatten(root["weather"] as JsonObject, "weather", classify: false);
        var plan = root["plan"] as JsonObject;
        var planFields = Flatten(plan, "plan", classify: false);
        var todosText = DescribeTodos(plan?["todos"] as JsonArray);

        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "schema", "schema_version", "updated", "status", "weather", "plan", "quota"
        };
        var otherFieldCount = root.Count(pair => !known.Contains(pair.Key));

        return new XbrdPanelSnapshot(
            true,
            schema,
            version,
            ReadScalar(root["updated"]),
            statusFields,
            weatherFields,
            planFields,
            todosText,
            otherFieldCount,
            "",
            endpoint,
            DateTimeOffset.Now);
    }

    /// <summary>Flattens the scalar members of one panel section (arrays of scalars become "a / b").</summary>
    private static IReadOnlyList<XbrdPanelField> Flatten(JsonObject? source, string prefix, bool classify)
    {
        var fields = new List<XbrdPanelField>();
        if (source is null)
        {
            return fields;
        }

        Collect(source, prefix, classify, 1, fields);
        return fields;
    }

    private static void Collect(
        JsonObject source,
        string prefix,
        bool classify,
        int depth,
        List<XbrdPanelField> fields)
    {
        foreach (var pair in source)
        {
            // Depth 1 keeps the bare key (the section title already names the section);
            // deeper nesting is dotted so the label stays unambiguous.
            var label = depth <= 1
                ? pair.Key
                : string.Equals(prefix, pair.Key, StringComparison.OrdinalIgnoreCase)
                    ? pair.Key
                    : $"{prefix}.{pair.Key}";

            switch (pair.Value)
            {
                case JsonObject nested when depth < MaxDepth:
                    Collect(nested, label, classify, depth + 1, fields);
                    break;
                case JsonObject:
                    // Deeper than we render; reported by the caller's "missing" counts instead.
                    break;
                case JsonArray array:
                {
                    var text = string.Join(" / ", array.Select(ReadScalar).Where(static value => value.Length > 0));
                    if (text.Length > 0)
                    {
                        fields.Add(new XbrdPanelField(label, text, "", XbrdSeverity.Ready));
                    }

                    break;
                }
                default:
                {
                    var text = ReadScalar(pair.Value);
                    if (text.Length == 0)
                    {
                        break;
                    }

                    var severity = classify ? ClassifyStatus(text) : XbrdSeverity.Ready;
                    fields.Add(new XbrdPanelField(label, text, severity.ToToken(), severity));
                    break;
                }
            }
        }
    }

    private static string DescribeTodos(JsonArray? todos)
    {
        if (todos is null || todos.Count == 0)
        {
            return "";
        }

        var texts = new List<string>();
        foreach (var item in todos)
        {
            switch (item)
            {
                case JsonObject todo:
                {
                    var text = ReadScalar(todo["text"]);
                    var meta = ReadScalar(todo["meta"]);
                    if (text.Length > 0)
                    {
                        texts.Add(meta.Length > 0 ? $"{text}({meta})" : text);
                    }

                    break;
                }
                default:
                {
                    var text = ReadScalar(item);
                    if (text.Length > 0)
                    {
                        texts.Add(text);
                    }

                    break;
                }
            }
        }

        return texts.Count == 0 ? "" : $"todos {texts.Count} 项：{string.Join("；", texts)}";
    }

    /// <summary>Status token → pill severity. Unknown tokens degrade rather than fail the card.</summary>
    public static XbrdSeverity ClassifyStatus(string text)
    {
        var token = (text ?? "").Trim().ToLowerInvariant();
        if (token.Length == 0)
        {
            return XbrdSeverity.Degraded;
        }

        if (token.Contains("error", StringComparison.Ordinal) ||
            token.Contains("fail", StringComparison.Ordinal) ||
            token.Contains("auth", StringComparison.Ordinal))
        {
            return XbrdSeverity.Error;
        }

        if (decimal.TryParse(token.TrimEnd('%'), NumberStyles.Number, CultureInfo.InvariantCulture, out var numeric))
        {
            // Battery-style percentages.
            return numeric <= 5 ? XbrdSeverity.Error : numeric <= 20 ? XbrdSeverity.Degraded : XbrdSeverity.Ready;
        }

        return token switch
        {
            "ok" or "up" or "true" or "on" or "ready" or "normal" or "good" or "connected" => XbrdSeverity.Ready,
            _ => XbrdSeverity.Degraded
        };
    }

    private static string ReadScalar(JsonNode? node)
    {
        try
        {
            switch (node)
            {
                case null:
                    return "";
                case JsonValue value:
                    return value.GetValueKind() switch
                    {
                        JsonValueKind.String => value.GetValue<string>() ?? "",
                        JsonValueKind.True => "true",
                        JsonValueKind.False => "false",
                        JsonValueKind.Null => "",
                        _ => value.ToJsonString()
                    };
                case JsonArray array:
                    return string.Join(" / ", array.Select(ReadScalar).Where(static text => text.Length > 0));
                default:
                    return "";
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or NotSupportedException)
        {
            return "";
        }
    }

    private static int ReadInt(JsonNode? node)
    {
        var text = ReadScalar(node);
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }
}
