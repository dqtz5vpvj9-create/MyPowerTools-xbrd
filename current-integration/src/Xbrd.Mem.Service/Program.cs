using System.Globalization;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Xbrd.Mem.Service;

/// <summary>
/// xbrd.mem.service 骨架实现（t0）：常驻循环，按 XBRD_MEM_INTERVAL_SECONDS 向路由器发布
/// quota.mem 快照。契约见仓库根 CONTRACT.md 第 5/6 节。
/// 与旧计划任务的行为等价（同一 source id、同一 panel key、同一 ttl），但不为每个 tick
/// 拉起新进程。
/// </summary>
internal static class Program
{
    private const string SourceId = "quota.mem";
    private const string PanelKey = "mem";
    private const int DefaultIntervalSeconds = 300;
    private const int TtlSeconds = 300;

    private static async Task<int> Main(string[] args)
    {
        var publisher = (Environment.GetEnvironmentVariable("XBRD_PUBLISHER") ?? "http://ow.lixinrui000.cn:8080").TrimEnd('/');
        var intervalSeconds = ReadInt("XBRD_MEM_INTERVAL_SECONDS", DefaultIntervalSeconds);
        var dataRoot = Environment.GetEnvironmentVariable("MPT_TOOL_DATA_ROOT")
                       ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyPowerTools", "state", "tools", "xbrd");
        Directory.CreateDirectory(dataRoot);
        var heartbeatFile = Path.Combine(dataRoot, "xbrd.mem.service.heartbeat");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

        Console.WriteLine($"[xbrd.mem] started publisher={publisher} interval={intervalSeconds}s dataRoot={dataRoot}");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var interval = TimeSpan.FromSeconds(Math.Max(5, intervalSeconds));

        while (!cts.IsCancellationRequested)
        {
            var started = DateTimeOffset.Now;
            try
            {
                var snapshot = BuildSnapshot();
                var json = snapshot.ToJsonString();
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var response = await http.PostAsync($"{publisher}/api/v1/sources/{SourceId}/snapshot", content, cts.Token);
                var body = await response.Content.ReadAsStringAsync(cts.Token);
                Console.WriteLine($"[xbrd.mem] {(int)response.StatusCode} {Truncate(body, 200)}");
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // 采集失败不改变上一次成功快照；路由器按 TTL 判定 stale。
                Console.Error.WriteLine($"[xbrd.mem] publish failed: {ex.Message}");
            }

            try
            {
                await File.WriteAllTextAsync(heartbeatFile, started.ToString("O"), cts.Token);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[xbrd.mem] heartbeat write failed: {ex.Message}");
            }

            try
            {
                await Task.Delay(interval, cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        Console.WriteLine("[xbrd.mem] stopped");
        return 0;
    }

    private static JsonObject BuildSnapshot()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status))
        {
            throw new InvalidOperationException("GlobalMemoryStatusEx failed.");
        }

        const double gb = 1024d * 1024d * 1024d;
        var physicalUsedGb = (status.ullTotalPhys - status.ullAvailPhys) / gb;
        var commitLimitGb = status.ullTotalPageFile / gb;
        var commitUsedGb = (status.ullTotalPageFile - status.ullAvailPageFile) / gb;

        var quotaStatus = "OK";
        if (commitLimitGb > 0)
        {
            var ratio = commitUsedGb / commitLimitGb;
            if (ratio >= 0.95) quotaStatus = "L";
            else if (ratio >= 0.85) quotaStatus = "W";
        }

        static string F(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);

        var revision = $"mem-{F(physicalUsedGb)}-{F(commitUsedGb)}-{F(commitLimitGb)}-{quotaStatus}";

        return new JsonObject
        {
            ["schema"] = "xbrd.source.snapshot.v1",
            ["schema_version"] = 1,
            ["source_id"] = SourceId,
            ["kind"] = "quota",
            ["generated_at"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            ["ttl_s"] = TtlSeconds,
            ["status"] = "ok",
            ["error"] = "",
            ["revision"] = revision,
            ["changed_fields"] = new JsonArray(
                $"quota.{PanelKey}.left",
                $"quota.{PanelKey}.aux",
                $"quota.{PanelKey}.limit",
                $"quota.{PanelKey}.status"),
            ["panel_patch"] = new JsonObject
            {
                ["quota"] = new JsonObject
                {
                    [PanelKey] = new JsonObject
                    {
                        ["label"] = "MEM",
                        ["kind"] = "simple",
                        ["left"] = F(physicalUsedGb),
                        ["aux"] = F(commitUsedGb),
                        ["limit"] = F(commitLimitGb),
                        ["status"] = quotaStatus
                    }
                }
            }
        };
    }

    private static int ReadInt(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : fallback;

    private static string Truncate(string text, int max)
        => string.IsNullOrEmpty(text) || text.Length <= max ? text : text[..max];

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
