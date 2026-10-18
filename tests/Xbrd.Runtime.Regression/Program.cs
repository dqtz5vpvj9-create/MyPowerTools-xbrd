using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using MyPowerTools.Platform.Abstractions;
using Xbrd.ServiceCommon;
using Xbrd.Surface.Services;
using Xbrd.Surface.ViewModels;
using Mem = Xbrd.Mem.Service.Program;
using Codex = Xbrd.CodexQuota.Service.Program;

namespace XbrdRegression;

internal static class Tests
{
    private static int _passed, _failed;
    private static bool _listOnly;
    private static async Task<int> Main(string[] args)
    {
        _listOnly = args.Contains("--list", StringComparer.Ordinal);
        if (_listOnly) Console.WriteLine("MPT_TEST_CASES_V1");
        foreach (var source in new[] { "mem", "codex" })
        {
            await Case($"{source}: settings false suppresses collection and HTTP despite enabled env", async () =>
            {
                using var fixture = new Fixture(source);
                fixture.Settings(false, 17);
                await fixture.RunOnce();
                Equal(0, fixture.Collections, "collector calls");
                Equal(0, fixture.Http.Requests.Count, "HTTP calls");
                Equal("disabled", fixture.Heartbeat()["status"]!.GetValue<string>(), "heartbeat status");
            });
            await Case($"{source}: explicit false wins over invalid sibling settings", async () =>
            {
                foreach (var badPublisher in new[] { true, false })
                {
                    using var fixture = new Fixture(source);
                    var values = new JsonObject
                    {
                        [fixture.EnableKey] = false,
                        ["publisherUrl"] = badPublisher ? "not a URL" : "http://new.invalid",
                        [source == "mem" ? "memIntervalSeconds" : "codexIntervalSeconds"] = badPublisher ? JsonValue.Create(7) : JsonValue.Create("invalid")
                    };
                    File.WriteAllText(fixture.ValuesPath, values.ToJsonString());
                    await fixture.RunOnce();
                    Equal(0, fixture.Collections, "invalid sibling must not defeat disable");
                    Equal(0, fixture.Http.Requests.Count, "disabled HTTP calls");
                    Equal("disabled", fixture.Heartbeat()["status"]!.GetValue<string>(), "disabled status");
                    Equal("http://environment.invalid", fixture.Heartbeat()["publisher"]!.GetValue<string>(), "retain last-good publisher");
                    Equal(300, fixture.Heartbeat()["intervalSeconds"]!.GetValue<int>(), "retain last-good interval");
                }
            });
            await Case($"{source}: installed settings selects publisher and interval", async () =>
            {
                using var fixture = new Fixture(source);
                fixture.Settings(true, 7, "http://configured.invalid:1234/");
                await fixture.RunOnce();
                Equal(1, fixture.Collections, "collector calls");
                Equal($"http://configured.invalid:1234/api/v1/sources/quota.{source}/snapshot", fixture.Http.Requests.Single().Uri, "publisher");
                Equal(7, fixture.Heartbeat()["intervalSeconds"]!.GetValue<int>(), "interval");
            });
            await Case($"{source}: env fallback without settings", async () =>
            {
                using var fixture = new Fixture(source);
                await fixture.RunOnce();
                Equal(1, fixture.Collections, "collector calls");
                Equal($"http://environment.invalid/api/v1/sources/quota.{source}/snapshot", fixture.Http.Requests.Single().Uri, "publisher");
                Equal(300, fixture.Heartbeat()["intervalSeconds"]!.GetValue<int>(), "interval");
            });
            await Case($"{source}: explicit environment disabled without settings", async () =>
            {
                using var fixture = new Fixture(source);
                Environment.SetEnvironmentVariable(fixture.EnabledEnv, "false");
                await fixture.RunOnce();
                Equal(0, fixture.Collections, "collector calls");
                Equal(0, fixture.Http.Requests.Count, "HTTP calls");
            });
            await Case($"{source}: settings-file override and minimum interval", async () =>
            {
                using var fixture = new Fixture(source);
                fixture.Settings(true, 1);
                var separate = Path.Combine(fixture.Root, "custom-settings.json");
                File.Move(fixture.ValuesPath, separate);
                Environment.SetEnvironmentVariable("XBRD_SETTINGS_FILE", separate);
                await fixture.RunOnce();
                Equal(5, fixture.Heartbeat()["intervalSeconds"]!.GetValue<int>(), "interval");
            });
            await Case($"{source}: resident reload, atomic replacement and malformed update retention", () => CheckResidentReload(source));
            await Case($"{source}: service and Surface honor custom platform data root", async () =>
            {
                using var fixture = new Fixture(source);
                Environment.SetEnvironmentVariable("MPT_TOOL_DATA_ROOT", null);
                var program = source == "mem" ? typeof(Mem) : typeof(Codex);
                var resolve = program.GetMethod("ResolveDataRoot", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
                // Assert before running so the failing baseline never writes outside the fixture.
                Equal(fixture.DataRoot, (string)resolve.Invoke(null, null)!, "resolved service data root");
                fixture.Settings(false, 300);
                await fixture.RunOnce();
                Equal("disabled", fixture.Heartbeat()["status"]!.GetValue<string>(), "heartbeat in custom root");
                fixture.WriteControlHeartbeat();
                using var client = new XbrdUnitControlClient(TimeSpan.FromSeconds(1), new FakeHttp(), null);
                var result = await client.PublishNowAsync("quota." + source, CancellationToken.None);
                Check(result.Ok && result.Status == "trigger-queued", "Surface resolves the same custom root");
                Check(File.Exists(fixture.TriggerPath), "trigger in custom root");
            });
        }

        await Case("shared control: HTTP control wait supports maximum configured interval", async () =>
        {
            using var fixture = new Fixture("mem");
            using var cts = new CancellationTokenSource();
            using var control = new ControlPlane(fixture.Unit, "quota.mem", 19225, fixture.DataRoot,
                "xbrd.mem.trigger", "xbrd.mem.service.trigger");
            // Both service projects compile this same control implementation. Select its
            // genuine HTTP-mode wait branch without binding any port.
            typeof(ControlPlane).GetProperty("Mode")!.SetValue(control, "http");
            var waiting = control.WaitAsync(TimeSpan.FromSeconds(int.MaxValue), cts.Token);
            cts.Cancel();
            await waiting.WaitAsync(TimeSpan.FromSeconds(1));
        });

        foreach (var error in new[] { new string('a', 127), new string('a', 128), new string('a', 220), new string('中', 60), string.Concat(Enumerable.Repeat("😀", 40)), new string('a', 124) + "😀末" })
        {
            await Case($"codex: payload error bounded to 127 UTF-8 bytes ({Encoding.UTF8.GetByteCount(error)} byte input)", async () =>
            {
                using var fixture = new Fixture("codex") { ReadError = error };
                await fixture.RunOnce();
                var body = JsonNode.Parse(fixture.Http.Requests.Single().Body)!.AsObject();
                var actual = body["error"]!.GetValue<string>();
                Check(Encoding.UTF8.GetByteCount(actual) <= 127, $"actual {Encoding.UTF8.GetByteCount(actual)} bytes");
                Check(error.StartsWith(actual, StringComparison.Ordinal), "must retain an intact prefix");
                Check(!actual.Contains('\uFFFD'), "no broken surrogate replacement");
                Equal("degraded", body["status"]!.GetValue<string>(), "status");
                Equal(0, body["panel_patch"]!.AsObject().Count, "last-good panel preservation");
                Equal(0, body["changed_fields"]!.AsArray().Count, "changed fields");
                Equal(300, body["ttl_s"]!.GetValue<int>(), "TTL unchanged");
            });
        }
        await Case("codex: healthy payload fields and protocol preserved", async () =>
        {
            using var fixture = new Fixture("codex");
            await fixture.RunOnce();
            var payload = JsonNode.Parse(fixture.Http.Requests.Single().Body)!.AsObject();
            Equal("xbrd.source.snapshot.v1", payload["schema"]!.GetValue<string>(), "schema");
            Equal("ok", payload["status"]!.GetValue<string>(), "status");
            Equal(4, payload["changed_fields"]!.AsArray().Count, "changed fields");
            Equal("CDX", payload["panel_patch"]!["quota"]!["codex"]!["label"]!.GetValue<string>(), "label");
            Equal("double", payload["panel_patch"]!["quota"]!["codex"]!["kind"]!.GetValue<string>(), "kind");
            Equal(300, payload["ttl_s"]!.GetValue<int>(), "TTL");
        });

        foreach (var source in new[] { "mem", "codex" })
        {
            await Case($"{source}: fresh degraded heartbeat durably queues exact trigger, no HTTP", async () =>
            {
                using var fixture = new Fixture(source);
                fixture.WriteControlHeartbeat();
                using var client = new XbrdUnitControlClient(TimeSpan.FromSeconds(1), fixture.Http, fixture.DataRoot);
                var result = await client.PublishNowAsync("quota." + source, CancellationToken.None);
                Check(result.Ok && result.Skipped && result.Status == "trigger-queued", "must be queued, not published");
                Check(File.Exists(fixture.TriggerPath) && File.ReadAllText(fixture.TriggerPath).Length > 0, "durable trigger payload");
                Check(!result.Text.Contains("已发布"), "text must not claim publication");
                Equal(0, fixture.Http.Requests.Count, "must not contact occupied port");
                var first = File.GetLastWriteTimeUtc(fixture.TriggerPath);
                result = await client.PublishNowAsync("quota." + source, CancellationToken.None);
                Check(result.Ok && File.GetLastWriteTimeUtc(fixture.TriggerPath) > first, "second mtime strictly increases");
            });
        }
        await Case("trigger: stale heartbeat rejected without HTTP or write", async () =>
        {
            using var fixture = new Fixture("mem");
            fixture.WriteControlHeartbeat(DateTimeOffset.UtcNow.AddHours(-1));
            using var client = new XbrdUnitControlClient(TimeSpan.FromSeconds(1), fixture.Http, fixture.DataRoot);
            var result = await client.PublishNowAsync("quota.mem", CancellationToken.None);
            Check(!result.Ok && !File.Exists(fixture.TriggerPath), "reject stale unit");
            Equal(0, fixture.Http.Requests.Count, "no occupied-port POST");
        });
        await Case("trigger: huge interval cannot authorize a days-old heartbeat", async () =>
        {
            using var fixture = new Fixture("mem");
            fixture.WriteControlHeartbeat(DateTimeOffset.UtcNow.AddDays(-2));
            var heartbeat = fixture.Heartbeat();
            heartbeat["intervalSeconds"] = long.MaxValue;
            File.WriteAllText(fixture.HeartbeatPath, heartbeat.ToJsonString());
            using var client = new XbrdUnitControlClient(TimeSpan.FromSeconds(1), fixture.Http, fixture.DataRoot);
            var result = await client.PublishNowAsync("quota.mem", CancellationToken.None);
            Check(!result.Ok && !File.Exists(fixture.TriggerPath), "huge interval must not allow stale trigger");
            Equal(0, fixture.Http.Requests.Count, "no occupied-port POST");
        });
        await Case("trigger: write failure never reports queued", async () =>
        {
            using var fixture = new Fixture("mem");
            fixture.WriteControlHeartbeat();
            Directory.CreateDirectory(fixture.TriggerPath);
            using var client = new XbrdUnitControlClient(TimeSpan.FromSeconds(1), fixture.Http, fixture.DataRoot);
            var result = await client.PublishNowAsync("quota.mem", CancellationToken.None);
            Check(!result.Ok && result.Status != "trigger-queued", "no acknowledged trigger on write failure");
            Equal(0, fixture.Http.Requests.Count, "no occupied-port POST");
        });
        await Case("trigger: cancellation does not touch trigger or HTTP", async () =>
        {
            using var fixture = new Fixture("mem");
            fixture.WriteControlHeartbeat();
            using var client = new XbrdUnitControlClient(TimeSpan.FromSeconds(1), fixture.Http, fixture.DataRoot);
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            try { await client.PublishNowAsync("quota.mem", cts.Token); throw new Exception("cancellation was swallowed"); }
            catch (OperationCanceledException) { }
            Check(!File.Exists(fixture.TriggerPath), "cancelled trigger not created");
            Equal(0, fixture.Http.Requests.Count, "cancelled HTTP not issued");
        });
        await Case("control: ordinary HTTP disabled response is truthful", async () =>
        {
            using var fixture = new Fixture("mem");
            fixture.Http.ControlResponse = "{\"ok\":true,\"skipped\":true,\"status\":\"disabled\"}";
            using var client = new XbrdUnitControlClient(TimeSpan.FromSeconds(1), fixture.Http, fixture.DataRoot);
            var result = await client.PublishNowAsync("quota.mem", CancellationToken.None);
            Check(result.Ok && result.Skipped, "disabled accepted");
            Check(!result.Text.Contains("已排队") && !result.Text.Contains("已发布"), "disabled is neither queued nor published");
            Check(result.DetailText.Contains("来源已停用"), "disabled is not described as a concurrency skip");
        });
        await Case("Surface row: queued request never renders a successful publication", () =>
        {
            var summary = new XbrdSourceSummary("quota.mem", "quota", "ok", "ok", false, 0, 300, null, null, "", [], "");
            var row = new XbrdSourceRowViewModel(summary, new XbrdSourcesViewModel());
            row.SetOutcome(new XbrdPublishNowResult(true, "quota.mem", "", 0, "trigger-queued", "", "test-trigger", true));
            Equal("已排队", row.OutcomeLabel, "row label");
            Check(!row.OutcomeIsOk && !row.OutcomeIsError, "pending is neither completed success nor failure");
            Check(row.OutcomeText.Contains("尚未确认发布") && !row.OutcomeText.Contains("立即发布：成功"), "truthful row evidence");
            var copied = new XbrdSourceRowViewModel(summary, new XbrdSourcesViewModel());
            var method = typeof(XbrdSourceRowViewModel).GetMethod("AdoptTransientState", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            method.Invoke(copied, [row.OutcomeText, row.OutcomeIsError, "", false, true]);
            Equal("已排队", copied.OutcomeLabel, "pending survives refresh");
            return Task.CompletedTask;
        });
        await Case("control: unknown source never writes or calls HTTP", async () =>
        {
            using var fixture = new Fixture("mem");
            using var client = new XbrdUnitControlClient(TimeSpan.FromSeconds(1), fixture.Http, fixture.DataRoot);
            Check(!(await client.PublishNowAsync("quota.other", CancellationToken.None)).Ok, "unknown source rejected");
            Equal(0, fixture.Http.Requests.Count, "HTTP calls");
        });
        if (_listOnly) return 0;
        Console.WriteLine($"RESULT {_passed}/{_passed + _failed} passed; {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    private static async Task CheckResidentReload(string source)
    {
        using var fixture = new Fixture(source);
        fixture.Settings(false, 300);
        // Occupied test-only loopback port forces the actual ControlPlane's trigger-file mode.
        using var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        Environment.SetEnvironmentVariable("XBRD_CONTROL_PORT", ((IPEndPoint)blocker.LocalEndpoint).Port.ToString());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var running = fixture.Run(false, cts.Token);
        try
        {
            await Until(() => File.Exists(fixture.HeartbeatPath) && fixture.Heartbeat()["status"] is not null, "initial heartbeat");
            Equal("disabled", fixture.Heartbeat()["status"]!.GetValue<string>(), "initial disabled");
            Equal(0, fixture.Collections, "initial collection");
            fixture.Settings(true, 11, "http://reloaded.invalid", atomic: true);
            await TriggerAndWait(fixture, () => fixture.Collections == 1);
            Equal("http://reloaded.invalid", fixture.Heartbeat()["publisher"]!.GetValue<string>(), "updated publisher");
            Equal(11, fixture.Heartbeat()["intervalSeconds"]!.GetValue<int>(), "updated interval");
            fixture.Settings(false, 13, "http://disabled.invalid", atomic: true);
            await TriggerAndWait(fixture, () => fixture.Heartbeat()["status"]!.GetValue<string>() == "disabled");
            Equal(1, fixture.Collections, "disable stops collections");
            foreach (var update in new[] { "{\"enable", "{\"publisherUrl\":\"http://wrong.invalid\",\"" + fixture.EnableKey + "\":\"bad\"}", "{\"publisherUrl\":\"invalid\",\"" + fixture.EnableKey + "\":true}", "{}" })
            {
                File.WriteAllText(fixture.ValuesPath, update);
                var oldTime = File.GetLastWriteTimeUtc(fixture.HeartbeatPath);
                await TriggerAndWait(fixture, () => File.GetLastWriteTimeUtc(fixture.HeartbeatPath) > oldTime);
                Equal("disabled", fixture.Heartbeat()["status"]!.GetValue<string>(), "bad update retains disabled");
                Equal("http://disabled.invalid", fixture.Heartbeat()["publisher"]!.GetValue<string>(), "bad update retains publisher");
                Equal(13, fixture.Heartbeat()["intervalSeconds"]!.GetValue<int>(), "bad update retains interval");
                Equal(1, fixture.Collections, "bad update must not collect");
            }
            File.Delete(fixture.ValuesPath);
            var old = File.GetLastWriteTimeUtc(fixture.HeartbeatPath);
            await TriggerAndWait(fixture, () => File.GetLastWriteTimeUtc(fixture.HeartbeatPath) > old);
            Equal("disabled", fixture.Heartbeat()["status"]!.GetValue<string>(), "missing update retains disabled");
        }
        finally
        {
            cts.Cancel();
            await running.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    private static async Task TriggerAndWait(Fixture fixture, Func<bool> ready)
    {
        // Exercise the production Surface file path against the production service watcher.
        var oldTime = File.GetLastWriteTimeUtc(fixture.HeartbeatPath);
        using var client = new XbrdUnitControlClient(TimeSpan.FromSeconds(1), new FakeHttp(), fixture.DataRoot);
        var result = await client.PublishNowAsync("quota." + fixture.Source, CancellationToken.None);
        Check(result.Ok && result.Status == "trigger-queued", "trigger queued");
        await Until(() => ready() && File.GetLastWriteTimeUtc(fixture.HeartbeatPath) > oldTime &&
            fixture.Heartbeat()["status"] is not null, "trigger response heartbeat");
    }

    private static async Task Until(Func<bool> predicate, string description)
    {
        var deadline = DateTime.UtcNow.AddSeconds(4);
        while (DateTime.UtcNow < deadline)
        {
            try { if (predicate()) return; } catch (IOException) { } catch (System.Text.Json.JsonException) { }
            await Task.Delay(20);
        }
        throw new Exception("timed out: " + description);
    }

    private static async Task Case(string name, Func<Task> test)
    {
        if (_listOnly) { Console.WriteLine(name); return; }
        try { await test(); _passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { _failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
    }
    private static void Equal<T>(T expected, T actual, string detail) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"{detail}: expected {expected}, got {actual}");
    private static void Check(bool condition, string detail) { if (!condition) throw new Exception(detail); }

    private sealed class Fixture : IDisposable
    {
        private readonly Dictionary<string, string?> _saved = new();
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "xbrd-regression-" + Guid.NewGuid().ToString("N"));
        public string Source { get; }
        public string Unit => Source == "mem" ? "xbrd.mem.service" : "xbrd.codex-quota.service";
        public string EnabledEnv => Source == "mem" ? "XBRD_MEM_ENABLED" : "XBRD_CODEX_ENABLED";
        public string EnableKey => Source == "mem" ? "enableMemSource" : "enableCodexSource";
        public string DataRoot => Path.Combine(Root, "data", "state", "tools", "xbrd");
        public string ValuesPath => Path.Combine(Root, "install", "modules", "xbrd", "settings.json");
        public string HeartbeatPath => Path.Combine(DataRoot, Unit + ".heartbeat");
        public string TriggerPath => Path.Combine(DataRoot, Source == "mem" ? "xbrd.mem.trigger" : "xbrd.codex-quota.trigger");
        public FakeHttp Http { get; } = new();
        public int Collections;
        public string? ReadError;
        public Fixture(string source)
        {
            Source = source;
            foreach (var name in new[] { "MPT_TOOL_DATA_ROOT", "MPT_DATA_ROOT", "MPT_INSTALL_ROOT", "XBRD_SETTINGS_FILE", "XBRD_PUBLISHER", "XBRD_MEM_ENABLED", "XBRD_CODEX_ENABLED", "XBRD_ENABLE_MEM_SOURCE", "XBRD_ENABLE_CODEX_SOURCE", "XBRD_MEM_INTERVAL_SECONDS", "XBRD_CODEX_INTERVAL_SECONDS", "XBRD_CONTROL_PORT" })
            {
                _saved[name] = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, null);
            }
            // The test process must neither use nor inspect any inherited router credential.
            Environment.SetEnvironmentVariable("XBRD_ROUTER_TOKEN", null);
            Directory.CreateDirectory(DataRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(ValuesPath)!);
            Environment.SetEnvironmentVariable("MPT_TOOL_DATA_ROOT", DataRoot);
            Environment.SetEnvironmentVariable("MPT_DATA_ROOT", Path.Combine(Root, "data"));
            Environment.SetEnvironmentVariable("MPT_INSTALL_ROOT", Path.Combine(Root, "install"));
            Environment.SetEnvironmentVariable("XBRD_PUBLISHER", "http://environment.invalid");
            Environment.SetEnvironmentVariable(EnabledEnv, "true");
        }
        public void Settings(bool enabled, int interval, string publisher = "http://configured.invalid", bool atomic = false)
        {
            var values = new JsonObject { [EnableKey] = enabled, [Source == "mem" ? "memIntervalSeconds" : "codexIntervalSeconds"] = interval, ["publisherUrl"] = publisher };
            var path = atomic ? ValuesPath + ".tmp" : ValuesPath;
            File.WriteAllText(path, values.ToJsonString());
            if (atomic) File.Move(path, ValuesPath, true);
        }
        public Task<int> RunOnce() => Run(true, CancellationToken.None);
        public Task<int> Run(bool once, CancellationToken token)
        {
            var args = once ? new[] { "--once" } : Array.Empty<string>();
            return Source == "mem"
                ? Mem.RunAsync(args, Http, () => { Collections++; return new JsonObject { ["revision"] = "fake-mem" }; }, token)
                : Codex.RunAsync(args, Http, _ =>
                {
                    Collections++;
                    if (ReadError is not null) throw new InvalidOperationException(ReadError);
                    return Task.FromResult<CodexQuotaSnapshot?>(new(new(12, 300, DateTimeOffset.UtcNow.AddHours(3)), new(30, 10080, DateTimeOffset.UtcNow.AddDays(2)), "app-server"));
                }, token);
        }
        public JsonObject Heartbeat() => JsonNode.Parse(File.ReadAllText(HeartbeatPath))!.AsObject();
        public void WriteControlHeartbeat(DateTimeOffset? observed = null) => File.WriteAllText(HeartbeatPath, new JsonObject
        {
            ["unit"] = Unit, ["control"] = "trigger-file", ["observedAt"] = (observed ?? DateTimeOffset.UtcNow).ToString("O"), ["intervalSeconds"] = 300
        }.ToJsonString());
        public void Dispose()
        {
            foreach (var (key, value) in _saved) Environment.SetEnvironmentVariable(key, value);
            Directory.Delete(Root, true);
        }
    }

    internal sealed class FakeHttp : HttpMessageHandler
    {
        public List<(string Uri, string Body)> Requests { get; } = new();
        public string ControlResponse = "{\"ok\":false,\"error\":\"test-only unavailable control endpoint\"}";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add((request.RequestUri!.ToString(), request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri.AbsolutePath == "/publish-now" ? ControlResponse : "{\"accepted\":true}")
            };
        }
    }
}
