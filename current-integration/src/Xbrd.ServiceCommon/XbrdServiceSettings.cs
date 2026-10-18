using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Xbrd.ServiceCommon;

/// <summary>Non-secret settings shared by the two resident publishers.</summary>
internal sealed record XbrdServiceSettings(string Publisher, bool Enabled, int IntervalSeconds);

/// <summary>
/// Reads the installed tool's values file at a publish boundary, never on a polling timer.
/// File settings override the manifest's environment defaults. Missing fields and incomplete,
/// unreadable or malformed updates keep the last known good configuration, including disabled.
/// No secret-store values are read or copied into the environment.
/// </summary>
internal sealed class XbrdServiceSettingsReader
{
    private readonly string _path;
    private readonly string _enabledKey;
    private readonly string _intervalKey;
    private XbrdServiceSettings _current;

    public XbrdServiceSettingsReader(string source, XbrdServiceSettings environmentDefaults)
    {
        _enabledKey = source == "mem" ? "enableMemSource" : "enableCodexSource";
        _intervalKey = source == "mem" ? "memIntervalSeconds" : "codexIntervalSeconds";
        _path = ResolveSettingsPath();
        _current = environmentDefaults;
    }

    internal static string ResolveSettingsPath()
    {
        var configured = Environment.GetEnvironmentVariable("XBRD_SETTINGS_FILE");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured.Trim());
        }

        var installRoot = Environment.GetEnvironmentVariable("MPT_INSTALL_ROOT");
        if (!string.IsNullOrWhiteSpace(installRoot))
        {
            return Path.Combine(installRoot.Trim(), "modules", "xbrd", "settings.json");
        }

        // Installed layout: <root>/service-units/<unit>/bin/<executable>. Do not resolve
        // against the process working directory, which may belong to the service manager.
        var bin = new DirectoryInfo(AppContext.BaseDirectory);
        var units = bin.Parent?.Parent;
        return units is { Parent: { } root } &&
               string.Equals(bin.Name, "bin", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(units.Name, "service-units", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(root.FullName, "modules", "xbrd", "settings.json")
            : "";
    }

    public XbrdServiceSettings Read()
    {
        if (_path.Length == 0)
        {
            return _current;
        }

        try
        {
            // Parse a whole JSON object first: the Shell can temporarily expose a truncated
            // file. A valid explicit false always wins; enabling/changing other values still
            // requires a fully validated update, so a bad URL cannot defeat a user's disable.
            if (JsonNode.Parse(File.ReadAllText(_path)) is not JsonObject values)
            {
                return _current;
            }

            var enabled = _current.Enabled;
            if (values.TryGetPropertyValue(_enabledKey, out var enabledNode))
            {
                if (!bool.TryParse(enabledNode?.ToString(), out enabled))
                {
                    return _current;
                }
                if (!enabled)
                {
                    _current = _current with { Enabled = false };
                }
            }

            var publisher = _current.Publisher;
            var interval = _current.IntervalSeconds;
            if (values.TryGetPropertyValue("publisherUrl", out var publisherNode))
            {
                if (publisherNode is not JsonValue pv || !pv.TryGetValue<string>(out var text) ||
                    string.IsNullOrWhiteSpace(text) ||
                    !Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) ||
                    uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
                {
                    return _current;
                }
                publisher = text.Trim().TrimEnd('/');
            }

            if (values.TryGetPropertyValue(_intervalKey, out var intervalNode))
            {
                if (!int.TryParse(intervalNode?.ToString(), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out interval))
                {
                    return _current;
                }
                interval = Math.Max(5, interval);
            }

            _current = new XbrdServiceSettings(publisher, enabled, interval);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            // Keep known configuration; a failed read must never silently re-enable a source.
        }

        return _current;
    }
}
