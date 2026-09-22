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
/// One <c>quota</c> entry of the panel document. The property set is discovered from the payload
/// (proxy/mes/mem/codex/glm/deepseek/lab today, anything else tomorrow) — no key list is hardcoded.
/// </summary>
public sealed record XbrdPanelQuotaRow(
    string Key,
    string Label,
    string Left,
    string Aux,
    string Status,
    string Detail,
    XbrdSeverity Severity)
{
    public bool IsReady => Severity == XbrdSeverity.Ready;

    public bool IsDegraded => Severity == XbrdSeverity.Degraded;

    public bool IsError => Severity == XbrdSeverity.Error;

    public string KeyText => string.Equals(Key, Label, StringComparison.OrdinalIgnoreCase) ? "" : Key;
}

/// <summary>
/// Read-only projection of <c>GET {publisherUrl}/panel.json</c> (schema <c>xbrd.panel.v1</c>).
/// The router has no HTML page — <c>/panel</c> serves this same JSON — so this card is the only
/// place the panel's real content is visible inside the tool.
/// </summary>
public sealed record XbrdPanelSnapshot(
    bool Ok,
    string Schema,
    int SchemaVersion,
    string Updated,
    IReadOnlyList<XbrdPanelField> StatusFields,
    IReadOnlyList<XbrdPanelField> WeatherFields,
    IReadOnlyList<XbrdPanelField> PlanFields,
    IReadOnlyList<XbrdPanelQuotaRow> QuotaRows,
    string TodosText,
    int OtherFieldCount,
    string Error,
    string Endpoint,
    DateTimeOffset FetchedAt)
{
    public static XbrdPanelSnapshot Empty { get; } = Failed("", "尚未读取。");

    public static XbrdPanelSnapshot Failed(string endpoint, string error) =>
        new(false, "", 0, "", [], [], [], [], "", 0, error, endpoint, DateTimeOffset.Now);

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

    public bool HasQuota => QuotaRows.Count > 0;

    public bool HasError => !Ok && Error.Length > 0;

    public string UpdatedText => Updated.Length == 0 ? "—" : Updated;

    public string SchemaText => Schema.Length == 0 ? "—" : $"{Schema} v{SchemaVersion}";

    public string CountsText =>
        $"status {StatusFields.Count} · weather {WeatherFields.Count} · plan {PlanFields.Count} · quota {QuotaRows.Count}" +
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
        var quotaRows = ParseQuota(root["quota"] as JsonObject);

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
            quotaRows,
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

    private static IReadOnlyList<XbrdPanelQuotaRow> ParseQuota(JsonObject? quota)
    {
        var rows = new List<XbrdPanelQuotaRow>();
        if (quota is null)
        {
            return rows;
        }

        foreach (var pair in quota)
        {
            if (pair.Value is JsonObject entry)
            {
                rows.Add(BuildQuotaRow(pair.Key, entry));
                continue;
            }

            var scalar = ReadScalar(pair.Value);
            if (scalar.Length > 0)
            {
                rows.Add(new XbrdPanelQuotaRow(pair.Key, pair.Key, scalar, "", "", "", ClassifyStatus(scalar)));
            }
        }

        rows.Sort(static (left, right) => string.CompareOrdinal(left.Key, right.Key));
        return rows;
    }

    private static XbrdPanelQuotaRow BuildQuotaRow(string key, JsonObject entry)
    {
        var label = ReadScalar(entry["label"]);
        if (label.Length == 0)
        {
            label = key;
        }

        var status = ReadScalar(entry["status"]);
        var left = ReadScalar(entry["left"]);
        if (left.Length == 0)
        {
            // "double" quota entries carry d7_left/h5_left instead of left.
            left = ReadScalar(entry["h5_left"]);
            if (left.Length == 0)
            {
                left = ReadScalar(entry["d7_left"]);
            }
        }

        var aux = ReadScalar(entry["aux"]);

        // Everything else the entry carries, in payload order, minus the columns already shown.
        var consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "label", "status", "left", "aux", "kind", "h5_left", "d7_left"
        };
        var detail = new List<string>();
        foreach (var pair in entry)
        {
            if (consumed.Contains(pair.Key))
            {
                continue;
            }

            var text = ReadScalar(pair.Value);
            if (text.Length > 0)
            {
                detail.Add($"{pair.Key}={text}");
            }
        }

        var kind = ReadScalar(entry["kind"]);
        if (kind.Length > 0)
        {
            detail.Insert(0, $"kind={kind}");
        }

        return new XbrdPanelQuotaRow(
            key,
            label,
            left.Length == 0 ? "—" : left,
            aux,
            status.Length == 0 ? "—" : status,
            string.Join(" · ", detail),
            status.Length == 0 ? XbrdSeverity.Degraded : ClassifyStatus(status));
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
