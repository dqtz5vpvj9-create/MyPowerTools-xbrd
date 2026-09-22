using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Xbrd.Surface.Services;

/// <summary>Outcome of one local process launch (no console window is ever created).</summary>
public sealed record XbrdProcessResult(
    string CommandLine,
    int ExitCode,
    bool TimedOut,
    string StandardOutput,
    string StandardError,
    string StartError)
{
    public bool Started => StartError.Length == 0;

    public bool Succeeded => Started && !TimedOut && ExitCode == 0;

    public string Summary => !Started
        ? $"无法启动：{StartError}"
        : TimedOut
            ? "超时未结束（已终止进程）"
            : ExitCode == 0
                ? "完成（exit 0）"
                : $"失败（exit {ExitCode}）";

    public string CombinedOutput
    {
        get
        {
            var builder = new StringBuilder();
            if (StandardOutput.Trim().Length > 0)
            {
                builder.Append(StandardOutput.Trim());
            }

            if (StandardError.Trim().Length > 0)
            {
                if (builder.Length > 0)
                {
                    builder.AppendLine();
                }

                builder.Append("[stderr] ").Append(StandardError.Trim());
            }

            return builder.ToString();
        }
    }

    public string Tail(int maxCharacters = 4000)
    {
        var text = CombinedOutput;
        if (text.Length <= maxCharacters)
        {
            return text;
        }

        return "…" + text[^maxCharacters..];
    }
}

/// <summary>
/// Launches local helper processes for the Surface buttons. Every launch sets
/// <c>UseShellExecute=false</c> and <c>CreateNoWindow=true</c> so no console window can steal focus
/// (AGENTS.md「构建与测试不得弹出命令行窗口」).
/// </summary>
public static class XbrdProcessRunner
{
    public static async Task<XbrdProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var display = new StringBuilder(fileName);
        foreach (var argument in arguments)
        {
            display.Append(' ').Append(argument.Contains(' ') ? '"' + argument + '"' : argument);
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                stdout.AppendLine(args.Data);
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                stderr.AppendLine(args.Data);
            }
        };

        try
        {
            if (!process.Start())
            {
                return new XbrdProcessResult(display.ToString(), -1, false, "", "", "进程未能启动。");
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return new XbrdProcessResult(display.ToString(), -1, false, "", "", ex.Message);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var timedOut = false;
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout <= TimeSpan.Zero ? TimeSpan.FromMinutes(1) : timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            TryKill(process);
        }

        try
        {
            process.WaitForExit(5000);
            // Second, parameterless wait flushes the asynchronous output handlers.
            process.WaitForExit();
        }
        catch (SystemException)
        {
            // The process already exited; buffered output below is still what we captured.
        }

        return new XbrdProcessResult(
            display.ToString(),
            timedOut ? -1 : process.ExitCode,
            timedOut,
            stdout.ToString(),
            stderr.ToString(),
            "");
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Best effort: the process may have exited between the check and the kill.
        }
    }

    /// <summary>Windows PowerShell path, falling back to PATH resolution.</summary>
    public static string ResolvePowerShell()
    {
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (!string.IsNullOrWhiteSpace(system32))
        {
            var candidate = Path.Combine(system32, "WindowsPowerShell", "v1.0", "powershell.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return "powershell.exe";
    }

    public static string ResolveSchtasks()
    {
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (!string.IsNullOrWhiteSpace(system32))
        {
            var candidate = Path.Combine(system32, "schtasks.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return "schtasks.exe";
    }

    public static string ResolveExplorer()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrWhiteSpace(windows))
        {
            var candidate = Path.Combine(windows, "explorer.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return "explorer.exe";
    }
}

/// <summary>A file in the UI Quota Worker log directory.</summary>
public sealed record XbrdLogFile(string Path, string Name, DateTimeOffset WrittenAt, long SizeBytes)
{
    public string Text => $"{Name} · {WrittenAt.ToLocalTime():MM-dd HH:mm:ss} · {SizeBytes / 1024.0:0.0} KB";
}

/// <summary>
/// Status of the daily <c>\XBRD\XBRD UI Quota Worker</c> scheduled task. Read with
/// <c>schtasks /Query</c>; a missing task or unparsable output degrades instead of throwing.
/// </summary>
public sealed record XbrdUiQuotaTaskStatus(
    bool Queried,
    string State,
    string LastRunTime,
    string LastResult,
    string NextRunTime,
    string TaskToRun,
    string Note,
    IReadOnlyList<XbrdLogFile> LogFiles)
{
    public const string TaskPathAndName = @"\XBRD\XBRD UI Quota Worker";

    public static XbrdUiQuotaTaskStatus Failed(string note, IReadOnlyList<XbrdLogFile> logFiles) =>
        new(false, "", "", "", "", "", note, logFiles);

    public bool Available => Queried;

    public string StateLabel => !Queried || State.Length == 0 ? "未知" : State;

    public XbrdSeverity Severity
    {
        get
        {
            if (!Queried)
            {
                return XbrdSeverity.Degraded;
            }

            if (LastResult.Length > 0 && !string.Equals(LastResult, "0", StringComparison.Ordinal))
            {
                return XbrdSeverity.Degraded;
            }

            return string.Equals(State, "Ready", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(State, "Running", StringComparison.OrdinalIgnoreCase)
                ? XbrdSeverity.Ready
                : XbrdSeverity.Degraded;
        }
    }

    public string PillToken => Severity.ToToken();

    public bool IsReady => Severity == XbrdSeverity.Ready;

    public bool IsDegraded => Severity == XbrdSeverity.Degraded;

    public bool IsError => Severity == XbrdSeverity.Error;

    public string DetailText => !Queried
        ? Note
        : $"最近运行 {LastRunTime} · 结果 {LastResult} · 下次 {NextRunTime}";

    public string LastResultLabel => !Queried
        ? Note
        : string.Equals(LastResult, "0", StringComparison.Ordinal)
            ? "0（成功）"
            : $"{LastResult}（需检查）";

    public string TaskToRunText => !Queried || TaskToRun.Length == 0 ? "—" : TaskToRun;

    public string LatestLogText => LogFiles.Count == 0
        ? "（暂无日志）"
        : string.Join("\n", LogFiles.Take(4).Select(static file => file.Text));

    public string LogDirectoryText { get; init; } = "";
}

/// <summary>
/// Local (machine-side) actions the HTTP command surface cannot express: running the UI quota
/// collector, opening the quota verification Edge window, restarting Service Units and reading logs.
/// Implements CONTRACT.md §4 「不放进 tool.json 的动作」.
/// </summary>
public sealed class XbrdLocalToolbox
{
    public const string WorkerTaskName = XbrdUiQuotaTaskStatus.TaskPathAndName;

    public static string UiQuotaStateRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "XBRD",
        "ui-quota-worker");

    public static string LogDirectory => Path.Combine(UiQuotaStateRoot, "logs");

    public static string RunnerScriptPath => Path.Combine(UiQuotaStateRoot, "run-ui-quota-worker.ps1");

    public static string VenvPythonPath => Path.Combine(UiQuotaStateRoot, ".venv", "Scripts", "python.exe");

    public string WorkerLogDirectory => LogDirectory;

    /// <summary>True when either invocation path for the collector exists.</summary>
    public bool IsCollectorInstalled => File.Exists(RunnerScriptPath) || File.Exists(VenvPythonPath);

    public string CollectorPathText => File.Exists(RunnerScriptPath)
        ? RunnerScriptPath
        : File.Exists(VenvPythonPath)
            ? VenvPythonPath
            : $"{RunnerScriptPath}（未安装）";

    /// <summary>
    /// Runs the collector once. Prefers the installed runner script (which also writes the worker
    /// log files); falls back to the venv interpreter + worker script from {xbrdRepoRoot}.
    /// </summary>
    public async Task<XbrdProcessResult> CollectUiQuotaAsync(
        XbrdSurfaceSettings settings,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (File.Exists(RunnerScriptPath))
        {
            return await XbrdProcessRunner.RunAsync(
                XbrdProcessRunner.ResolvePowerShell(),
                ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", RunnerScriptPath],
                UiQuotaStateRoot,
                timeout,
                cancellationToken).ConfigureAwait(false);
        }

        var worker = Path.Combine(settings.XbrdRepoRoot, "tools", "xbrd_ui_quota_worker.py");
        var envFile = Path.Combine(settings.XbrdRepoRoot, "secrets", "xbrd_ui_quota_worker.env");
        if (!File.Exists(VenvPythonPath))
        {
            return new XbrdProcessResult(
                "xbrd_ui_quota_worker.py",
                -1,
                false,
                "",
                "",
                $"未找到采集入口。期望 {RunnerScriptPath}，或 {VenvPythonPath} + {worker}。");
        }

        if (!File.Exists(worker))
        {
            return new XbrdProcessResult(
                worker,
                -1,
                false,
                "",
                "",
                $"采集脚本不存在：{worker}（检查设置 xbrdRepoRoot）。");
        }

        var arguments = new List<string> { "-u", worker, "--env-file", envFile };
        return await XbrdProcessRunner.RunAsync(
            VenvPythonPath,
            arguments,
            settings.XbrdRepoRoot,
            timeout,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Opens the quota verification Edge window via the xbrd repo script.</summary>
    public async Task<XbrdProcessResult> OpenQuotaVerificationWindowAsync(
        XbrdSurfaceSettings settings,
        CancellationToken cancellationToken)
    {
        var script = settings.ScriptPath("xbrd-start-ui-quota-edge.ps1");
        if (!File.Exists(script))
        {
            return new XbrdProcessResult(
                script,
                -1,
                false,
                "",
                "",
                $"未找到 {script}（检查设置 xbrdRepoRoot）。");
        }

        return await XbrdProcessRunner.RunAsync(
            XbrdProcessRunner.ResolvePowerShell(),
            ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Visible"],
            settings.XbrdRepoRoot,
            TimeSpan.FromMinutes(3),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Opens a directory in Explorer (GUI app: no console window suppression needed).</summary>
    public Task<XbrdProcessResult> OpenDirectoryAsync(string directory, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return Task.FromResult(new XbrdProcessResult(
                directory,
                -1,
                false,
                "",
                "",
                $"目录不存在：{(directory.Length == 0 ? "(空)" : directory)}"));
        }

        return XbrdProcessRunner.RunAsync(
            XbrdProcessRunner.ResolveExplorer(),
            [directory],
            "",
            TimeSpan.FromSeconds(20),
            cancellationToken);
    }

    public IReadOnlyList<XbrdLogFile> ListLogFiles()
    {
        try
        {
            if (!Directory.Exists(LogDirectory))
            {
                return [];
            }

            return Directory
                .EnumerateFiles(LogDirectory, "*", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .Where(static info => info.Exists)
                .OrderByDescending(static info => info.LastWriteTimeUtc)
                .Take(12)
                .Select(static info => new XbrdLogFile(
                    info.FullName,
                    info.Name,
                    new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                    info.Length))
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }
    }

    /// <summary>
    /// Queries the daily UI Quota task. Primary probe is <c>schtasks /Query /TN … /FO LIST /V</c>; its
    /// output language/encoding depends on how the process is launched (a GUI host gets the system
    /// UI language), so a <c>Get-ScheduledTask</c> JSON probe backs it up. Both paths degrade with a
    /// reason instead of throwing.
    /// </summary>
    public async Task<XbrdUiQuotaTaskStatus> QueryUiQuotaTaskAsync(CancellationToken cancellationToken)
    {
        var logFiles = ListLogFiles();

        var viaSchtasks = await TryQueryWithSchtasksAsync(logFiles, cancellationToken).ConfigureAwait(false);
        if (viaSchtasks.Status is not null)
        {
            return viaSchtasks.Status;
        }

        var viaPowerShell = await TryQueryWithPowerShellAsync(logFiles, cancellationToken).ConfigureAwait(false);
        if (viaPowerShell.Status is not null)
        {
            return viaPowerShell.Status with
            {
                Note = $"schtasks 结果不可用（{viaSchtasks.Failure}），已改用 Get-ScheduledTask 读取。"
            };
        }

        return XbrdUiQuotaTaskStatus.Failed(
            $"计划任务状态读取失败：{viaSchtasks.Failure}；Get-ScheduledTask 回退也失败：{viaPowerShell.Failure}",
            logFiles)
        with
        {
            LogDirectoryText = LogDirectory
        };
    }

    private async Task<(XbrdUiQuotaTaskStatus? Status, string Failure)> TryQueryWithSchtasksAsync(
        IReadOnlyList<XbrdLogFile> logFiles,
        CancellationToken cancellationToken)
    {
        var result = await XbrdProcessRunner.RunAsync(
            XbrdProcessRunner.ResolveSchtasks(),
            ["/Query", "/TN", WorkerTaskName, "/FO", "LIST", "/V"],
            "",
            TimeSpan.FromSeconds(20),
            cancellationToken).ConfigureAwait(false);

        if (!result.Started)
        {
            return (null, $"schtasks 无法启动（{result.StartError}）");
        }

        var text = result.CombinedOutput;
        var state = ReadLabel(text, "Status", "状态");
        var lastRun = ReadLabel(text, "Last Run Time", "上次运行时间");
        var lastResult = ReadLabel(text, "Last Result", "上次结果");
        var nextRun = ReadLabel(text, "Next Run Time", "下次运行时间");
        var taskToRun = ReadLabel(text, "Task To Run", "要运行的任务");
        var taskState = ReadLabel(text, "Scheduled Task State", "计划任务状态");

        var parsed = !string.IsNullOrWhiteSpace(state) ||
                     !string.IsNullOrWhiteSpace(lastRun) ||
                     !string.IsNullOrWhiteSpace(taskState);
        if (!parsed)
        {
            var reason = result.ExitCode == 0
                ? "输出无法解析（语言/编码不受支持）"
                : $"退出码 {result.ExitCode}：{FirstLine(text)}";
            return (null, reason);
        }

        return (new XbrdUiQuotaTaskStatus(
            true,
            string.IsNullOrWhiteSpace(taskState) ? state : taskState,
            string.IsNullOrWhiteSpace(lastRun) ? "—" : lastRun,
            string.IsNullOrWhiteSpace(lastResult) ? "—" : lastResult,
            string.IsNullOrWhiteSpace(nextRun) ? "—" : nextRun,
            taskToRun,
            "schtasks /Query",
            logFiles)
        {
            LogDirectoryText = LogDirectory
        }, "");
    }

    private async Task<(XbrdUiQuotaTaskStatus? Status, string Failure)> TryQueryWithPowerShellAsync(
        IReadOnlyList<XbrdLogFile> logFiles,
        CancellationToken cancellationToken)
    {
        // ASCII-only JSON keys keep the probe independent of the shell's code page.
        const string script = """
            $ErrorActionPreference = 'SilentlyContinue'
            $t = Get-ScheduledTask -TaskPath '\XBRD\' -TaskName 'XBRD UI Quota Worker'
            if ($null -eq $t) { Write-Output '{"installed":false}'; exit 0 }
            $i = Get-ScheduledTaskInfo -TaskPath '\XBRD\' -TaskName 'XBRD UI Quota Worker'
            [pscustomobject]@{
              installed = $true
              state = [string]$t.State
              lastRun = [string]$i.LastRunTime
              lastResult = [string]$i.LastTaskResult
              nextRun = [string]$i.NextRunTime
              action = [string]$t.Actions[0].Execute
            } | ConvertTo-Json -Compress
            """;

        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var result = await XbrdProcessRunner.RunAsync(
            XbrdProcessRunner.ResolvePowerShell(),
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded],
            "",
            TimeSpan.FromSeconds(30),
            cancellationToken).ConfigureAwait(false);

        if (!result.Started || !result.Succeeded)
        {
            return (null, result.Started ? FirstLine(result.CombinedOutput) : result.StartError);
        }

        var json = result.StandardOutput.Trim();
        var start = json.IndexOf('{');
        var end = json.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return (null, "Get-ScheduledTask 未返回 JSON");
        }

        JsonObject? payload;
        try
        {
            payload = JsonNode.Parse(json[start..(end + 1)]) as JsonObject;
        }
        catch (JsonException)
        {
            payload = null;
        }

        if (payload is null)
        {
            return (null, "Get-ScheduledTask 返回的 JSON 无法解析");
        }

        if (!XbrdJson.ReadBool(payload, "installed"))
        {
            return (new XbrdUiQuotaTaskStatus(
                false,
                "",
                "",
                "",
                "",
                "",
                "Get-ScheduledTask 未找到该计划任务（可能未安装）。",
                logFiles)
            {
                LogDirectoryText = LogDirectory
            }, "");
        }

        return (new XbrdUiQuotaTaskStatus(
            true,
            XbrdJson.ReadString(payload, "state", "unknown"),
            XbrdJson.ReadString(payload, "lastRun", "—"),
            XbrdJson.ReadString(payload, "lastResult", "—"),
            XbrdJson.ReadString(payload, "nextRun", "—"),
            XbrdJson.ReadString(payload, "action"),
            "Get-ScheduledTask",
            logFiles)
        {
            LogDirectoryText = LogDirectory
        }, "");
    }

    private static string ReadLabel(string text, params string[] labels)
    {
        foreach (var line in text.Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var label = line[..colon].Trim();
            foreach (var candidate in labels)
            {
                if (string.Equals(label, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return line[(colon + 1)..].Trim();
                }
            }
        }

        return "";
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        return line is null or "" ? "(无输出)" : line;
    }
}

/// <summary>Unit log lines for the log area, gathered from the scoped Service Unit client.</summary>
public sealed record XbrdUnitLogEntry(DateTimeOffset Time, string UnitId, string Level, string Message)
{
    public string Line => string.Create(
        CultureInfo.InvariantCulture,
        $"{Time.ToLocalTime():MM-dd HH:mm:ss} [{UnitId}] {Level}: {Message}");
}
