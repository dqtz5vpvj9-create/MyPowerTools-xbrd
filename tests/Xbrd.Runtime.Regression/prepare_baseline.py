#!/usr/bin/env python3
"""Materialize pinned production sources with dependency-injection seams only.

No settings reload, payload-bound or trigger-fallback fixes are applied. Both runs
compile the same regression source. Baseline mutations merely allow fake collectors,
HTTP handlers and external cancellation without contacting a backend/user account.
The historical namespace-only control-plane copies are normalized to the current
shared-source layout; their behavior is unchanged and equality is asserted below.
"""
from pathlib import Path
import subprocess
import sys

root = Path(__file__).resolve().parents[2]
destination = Path(sys.argv[1]).resolve()
revision = "f39e43cf46f243e43f18781f9db3b870bdd32be0"
files = [
    "Xbrd.Mem.Service/Program.cs", "Xbrd.CodexQuota.Service/Program.cs",
    "Xbrd.Surface/Services/XbrdUnitControlClient.cs", "Xbrd.Surface/Services/XbrdControlModels.cs",
    "Xbrd.Surface/Services/XbrdSourceModels.cs", "Xbrd.Surface/ViewModels/XbrdSourceRowViewModel.cs",
]
for relative in files:
    text = subprocess.check_output(["git", "-C", str(root), "show", f"{revision}:current-integration/src/{relative}"], text=True)
    if relative.endswith("/Program.cs"):
        text = "using Xbrd.ServiceCommon;\n" + text
        memory = relative.startswith("Xbrd.Mem")
        signature = "    private static Task<int> Main(string[] args) => RunAsync(args);\n\n    internal static async Task<int> RunAsync(\n        string[] args,\n        HttpMessageHandler? httpHandler = null,\n"
        signature += "        Func<JsonObject>? collectSnapshot = null,\n" if memory else "        Func<CancellationToken, Task<CodexQuotaSnapshot?>>? readQuota = null,\n"
        signature += "        CancellationToken cancellationToken = default)\n    {"
        text = text.replace("    private static async Task<int> Main(string[] args)\n    {", signature)
        text = text.replace("using var cts = new CancellationTokenSource();", "using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);")
        text = text.replace("using var http = CreateHttpClient();", "using var http = CreateHttpClient(httpHandler);")
        text = text.replace("private static HttpClient CreateHttpClient()", "private static HttpClient CreateHttpClient(HttpMessageHandler? handler = null)")
        text = text.replace("var client = new HttpClient { Timeout = TimeSpan.FromSeconds(PublishTimeoutSeconds) };", "var client = handler is null ? new HttpClient() : new HttpClient(handler);\n        client.Timeout = TimeSpan.FromSeconds(PublishTimeoutSeconds);")
        if memory:
            text = text.replace("Console.WriteLine(BuildSnapshot().ToJsonString());", "Console.WriteLine((collectSnapshot?.Invoke() ?? BuildSnapshot()).ToJsonString());")
            text = text.replace("var snapshot = BuildSnapshot();", "var snapshot = collectSnapshot?.Invoke() ?? BuildSnapshot();")
        else:
            text = text.replace("ReadSnapshotAsync(CancellationToken.None)", "ReadSnapshotAsync(CancellationToken.None, readQuota)")
            text = text.replace("ReadSnapshotAsync(cts.Token)", "ReadSnapshotAsync(cts.Token, readQuota)")
            text = text.replace("ReadSnapshotAsync(CancellationToken cancellationToken)", "ReadSnapshotAsync(\n        CancellationToken cancellationToken,\n        Func<CancellationToken, Task<CodexQuotaSnapshot?>>? readQuota = null)")
            text = text.replace("read = await CodexQuotaReader.ReadAsync(cancellationToken).ConfigureAwait(false);", "read = readQuota is null\n                ? await CodexQuotaReader.ReadAsync(cancellationToken).ConfigureAwait(false)\n                : await readQuota(cancellationToken).ConfigureAwait(false);")
    if relative.endswith("XbrdUnitControlClient.cs"):
        text = text.replace('''    public XbrdUnitControlClient(TimeSpan timeout)
    {
        _http = new HttpClient
        {
            Timeout = timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(15) : timeout
        };
    }''', '''    public XbrdUnitControlClient(TimeSpan timeout) : this(timeout, null, null) { }

    internal XbrdUnitControlClient(TimeSpan timeout, HttpMessageHandler? handler, string? dataRoot)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(15) : timeout;
    }''')
    path = destination / relative
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text)
# Normalize the old namespace-only duplicates without importing any control fixes.
controls = [subprocess.check_output([
    "git", "-C", str(root), "show", f"{revision}:current-integration/src/Xbrd.{service}.Service/ControlPlane.cs"
], text=True).replace(f"namespace Xbrd.{service}.Service;", "namespace Xbrd.ServiceCommon;")
    .replace("Program.AsciiSafe(", "XbrdServiceLog.AsciiSafe(") for service in ("Mem", "CodexQuota")]
assert controls[0] == controls[1], "Historical control implementations must remain identical"
common = destination / "Xbrd.ServiceCommon"
common.mkdir(parents=True, exist_ok=True)
(common / "ControlPlane.cs").write_text(controls[0])
# Settings reader is unused by the baseline; logging is the unchanged ASCII formatter.
for name in ("XbrdServiceSettings.cs", "XbrdServiceLog.cs"):
    (common / name).write_text((root / "current-integration/src/Xbrd.ServiceCommon" / name).read_text())
print(f"Baseline {revision} with injection seams and equivalent shared-source layout prepared at {destination}")
