using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Xbrd.Mem.Service;

/// <summary>
/// Result of one publish attempt. Shared by the periodic tick and the control endpoint so both
/// report the same shape.
/// </summary>
internal sealed record PublishOutcome(
    bool Ok,
    bool Skipped,
    string Status,
    string Revision,
    long DurationMs,
    string Error,
    int? HttpStatus)
{
    /// <summary>Used when cancellation interrupted a publish; never recorded as "last result".</summary>
    public static PublishOutcome Aborted { get; } = new(false, true, "aborted", "", 0, "unit is shutting down", null);

    /// <summary>Initial <c>/state</c> value before the first publish of this process completes.</summary>
    public static PublishOutcome Idle { get; } = new(false, false, "idle", "", 0, "", null);

    public static PublishOutcome From(string status, string revision, int? httpStatus, long durationMs, string error)
    {
        // `disabled` is a deliberate no-op (CONTRACT section 5), not a failure: nothing is posted
        // and the request is reported as skipped.
        var skipped = string.Equals(status, "disabled", StringComparison.Ordinal);
        var ok = string.Equals(status, "ok", StringComparison.Ordinal) || skipped;
        return new PublishOutcome(ok, skipped, status, revision, durationMs, error, httpStatus);
    }

    public static PublishOutcome Busy(string revision, string status) => new(true, true, status, revision, 0, "", null);

    public static PublishOutcome TimedOut(string revision) =>
        new(false, true, "timeout", revision, 0, "publish-now timed out waiting for the unit loop", null);
}

/// <summary>
/// Loopback control plane for a Service Unit (CONTRACT.md section 5, optional/primary trigger).
///
/// <list type="bullet">
///   <item><c>POST /publish-now</c> asks the resident loop to run one extra publish immediately and
///         answers with <c>{ok, source_id, revision, duration_ms, status}</c>. The loop is the only
///         publisher, so the request and the periodic tick can never publish concurrently; a request
///         that arrives while a publish is running is answered with <c>skipped: true</c>.</item>
///   <item><c>GET /state</c> reports the control mode, the last result and liveness.</item>
///   <item>Binding is <c>IPAddress.Loopback</c> only, so the port is never reachable off-box.</item>
///   <item>If the port cannot be bound the unit degrades to watching
///         <c>%MPT_TOOL_DATA_ROOT%\xbrd.&lt;unit&gt;.trigger</c> mtime instead of crashing, and keeps
///         publishing on its normal interval.</item>
/// </list>
/// </summary>
internal sealed class ControlPlane : IDisposable
{
    private static readonly JsonSerializerOptions HttpJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly TimeSpan TriggerPollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly string _unitId;
    private readonly string _sourceId;
    private readonly int _port;
    private readonly string _triggerFile;
    private readonly string[] _triggerPaths;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private readonly object _gate = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _serverCts;
    private Task? _serverTask;
    private TaskCompletionSource<PublishOutcome>? _pending;
    private TaskCompletionSource? _wakeSignal;
    private PublishOutcome _last = PublishOutcome.Idle;
    private long _requestCount;
    private volatile bool _publishing;
    private bool _requested;
    private bool _triggerSeen;
    private DateTime _triggerMtime = DateTime.MinValue;

    public ControlPlane(
        string unitId,
        string sourceId,
        int defaultPort,
        string toolDataRoot,
        string primaryTriggerFileName,
        string aliasTriggerFileName)
    {
        _unitId = unitId;
        _sourceId = sourceId;
        _port = ReadPort(defaultPort);
        _triggerFile = Path.Combine(toolDataRoot, primaryTriggerFileName);
        _triggerPaths = new[]
        {
            _triggerFile,
            Path.Combine(toolDataRoot, aliasTriggerFileName)
        };

        PrimeTriggerState();
    }

    /// <summary>"starting" | "http" | "trigger-file".</summary>
    public string Mode { get; private set; } = "starting";

    public PublishOutcome Last
    {
        get
        {
            lock (_gate)
            {
                return _last;
            }
        }
    }

    public void Start()
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, _port);
            listener.Start();
            _listener = listener;
            Mode = "http";
            _serverCts = new CancellationTokenSource();
            _serverTask = Task.Run(() => AcceptLoopAsync(listener, _serverCts.Token));
            Console.WriteLine(Program.AsciiSafe(
                $"[{_unitId}] control endpoint listening on http://127.0.0.1:{_port} (POST /publish-now, GET /state)"));
        }
        catch (Exception ex)
        {
            // Port busy (or otherwise unusable) must degrade, never crash: the unit keeps its normal
            // interval publishing and additionally honours the trigger file.
            Mode = "trigger-file";
            _listener = null;
            Console.Error.WriteLine(Program.AsciiSafe(
                $"[{_unitId}] control port {_port} unavailable ({ex.Message}); degraded to trigger file {_triggerFile}"));
        }
    }

    /// <summary>Called by the loop at the start of every publish.</summary>
    public void BeginPublish()
    {
        lock (_gate)
        {
            _publishing = true;
            _requested = false;
        }
    }

    /// <summary>Called by the loop once the publish result is known; releases any waiting request.</summary>
    public void EndPublish(PublishOutcome outcome)
    {
        TaskCompletionSource<PublishOutcome>? pending;
        lock (_gate)
        {
            _publishing = false;
            if (!string.Equals(outcome.Status, "aborted", StringComparison.Ordinal))
            {
                _last = outcome;
            }

            pending = _pending;
            _pending = null;
        }

        pending?.TrySetResult(outcome);
    }

    /// <summary>
    /// Waits for the periodic interval, a control request, or (in the degraded mode) a trigger-file
    /// change. Returns when the caller should publish again or the token was cancelled.
    /// </summary>
    public async Task WaitAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + interval;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (IsRequested())
            {
                return;
            }

            if (Mode != "http" && TriggerFileChanged())
            {
                return;
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            // HTTP mode blocks for the whole remaining interval (the wake signal interrupts it); the
            // degraded mode polls the trigger file at a fixed cadence instead of waking per second
            // when HTTP is available.
            var slice = remaining;
            if (Mode != "http" && TriggerPollInterval < remaining)
            {
                slice = TriggerPollInterval;
            }
            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _wakeSignal = signal;
            try
            {
                await Task.WhenAny(Task.Delay(slice, cancellationToken), signal.Task).ConfigureAwait(false);
            }
            finally
            {
                if (ReferenceEquals(_wakeSignal, signal))
                {
                    _wakeSignal = null;
                }
            }
        }
    }

    public void Dispose()
    {
        try
        {
            _serverCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            _listener?.Stop();
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
        }

        try
        {
            _serverTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) when (ex is AggregateException or OperationCanceledException)
        {
        }

        _serverCts?.Dispose();
    }

    private int ReadPort(int defaultPort)
    {
        var raw = Environment.GetEnvironmentVariable("XBRD_CONTROL_PORT");
        if (!string.IsNullOrWhiteSpace(raw) &&
            int.TryParse(raw.Trim(), out var port) &&
            port is > 0 and <= 65535)
        {
            return port;
        }

        if (!string.IsNullOrWhiteSpace(raw))
        {
            Console.Error.WriteLine(Program.AsciiSafe(
                $"[{_unitId}] XBRD_CONTROL_PORT='{raw}' is not a valid port; using {defaultPort}"));
        }

        return defaultPort;
    }

    private bool IsRequested()
    {
        lock (_gate)
        {
            return _requested;
        }
    }

    private void PrimeTriggerState()
    {
        // Record what already exists at startup so a stale trigger file does not fire immediately.
        var newest = NewestTriggerMtime();
        _triggerSeen = newest is not null;
        _triggerMtime = newest ?? DateTime.MinValue;
    }

    private DateTime? NewestTriggerMtime()
    {
        DateTime? newest = null;
        foreach (var path in _triggerPaths)
        {
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                var modified = File.GetLastWriteTimeUtc(path);
                if (newest is null || modified > newest.Value)
                {
                    newest = modified;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return newest;
    }

    private bool TriggerFileChanged()
    {
        var newest = NewestTriggerMtime();
        if (newest is null)
        {
            _triggerSeen = false;
            _triggerMtime = DateTime.MinValue;
            return false;
        }

        if (!_triggerSeen)
        {
            _triggerSeen = true;
            _triggerMtime = newest.Value;
            return true;
        }

        if (newest.Value > _triggerMtime)
        {
            _triggerMtime = newest.Value;
            return true;
        }

        return false;
    }

    private async Task<PublishOutcome> RequestPublishAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource<PublishOutcome> pending;
        lock (_gate)
        {
            if (_publishing)
            {
                return PublishOutcome.Busy(_last.Revision, "in-progress");
            }

            if (_pending is not null)
            {
                return PublishOutcome.Busy(_last.Revision, "queued");
            }

            pending = new TaskCompletionSource<PublishOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending = pending;
            _requested = true;
        }

        Interlocked.Increment(ref _requestCount);
        _wakeSignal?.TrySetResult();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            return await pending.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_pending, pending))
                {
                    _pending = null;
                    _requested = false;
                }
            }

            return PublishOutcome.TimedOut(_last.Revision);
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                continue;
            }

            _ = HandleClientAsync(client, cancellationToken);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 5000;
                client.SendTimeout = 5000;
                var remote = client.Client.RemoteEndPoint as IPEndPoint;
                if (remote is not null && !IPAddress.IsLoopback(remote.Address))
                {
                    // Defensive: the listener is loopback-only, so this cannot happen.
                    return;
                }

                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
                var requestLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(requestLine))
                {
                    return;
                }

                var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                {
                    await WriteResponseAsync(stream, 400, "{\"ok\":false,\"error\":\"malformed request line\"}", cancellationToken).ConfigureAwait(false);
                    return;
                }

                var method = parts[0];
                var path = parts[1];
                var query = path.IndexOf('?', StringComparison.Ordinal);
                if (query >= 0)
                {
                    path = path[..query];
                }

                // Drain headers; requests carry no body.
                while (true)
                {
                    var header = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(header))
                    {
                        break;
                    }
                }

                if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(path, "/publish-now", StringComparison.OrdinalIgnoreCase))
                {
                    var outcome = await RequestPublishAsync(cancellationToken).ConfigureAwait(false);
                    await WriteResponseAsync(stream, 200, BuildPublishResponse(outcome), cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(path, "/state", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteResponseAsync(stream, 200, BuildStateResponse(), cancellationToken).ConfigureAwait(false);
                    return;
                }

                await WriteResponseAsync(
                    stream,
                    404,
                    "{\"ok\":false,\"error\":\"not found\",\"endpoints\":[\"POST /publish-now\",\"GET /state\"]}",
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine(Program.AsciiSafe($"[{_unitId}] control request failed: {ex.Message}"));
            }
        }
    }

    private string BuildPublishResponse(PublishOutcome outcome)
    {
        var json = new JsonObject
        {
            ["ok"] = outcome.Ok,
            ["skipped"] = outcome.Skipped,
            ["source_id"] = _sourceId,
            ["revision"] = outcome.Revision,
            ["duration_ms"] = outcome.DurationMs,
            ["status"] = outcome.Status,
            ["error"] = outcome.Error,
            ["http_status"] = outcome.HttpStatus,
            ["pid"] = Environment.ProcessId
        };
        return json.ToJsonString(HttpJsonOptions);
    }

    private string BuildStateResponse()
    {
        var last = Last;
        var triggers = new JsonArray();
        foreach (var path in _triggerPaths)
        {
            triggers.Add(path);
        }

        var json = new JsonObject
        {
            ["unit"] = _unitId,
            ["pid"] = Environment.ProcessId,
            ["source_id"] = _sourceId,
            ["control"] = Mode,
            ["port"] = Mode == "http" ? _port : null,
            ["trigger_file"] = _triggerFile,
            ["trigger_files_watched"] = triggers,
            ["publishing"] = _publishing,
            ["publish_requests"] = Interlocked.Read(ref _requestCount),
            ["uptime_s"] = (long)(DateTimeOffset.UtcNow - _startedAt).TotalSeconds,
            ["last"] = new JsonObject
            {
                ["ok"] = last.Ok,
                ["status"] = last.Status,
                ["revision"] = last.Revision,
                ["duration_ms"] = last.DurationMs,
                ["error"] = last.Error,
                ["http_status"] = last.HttpStatus
            }
        };
        return json.ToJsonString(HttpJsonOptions);
    }

    private static async Task WriteResponseAsync(Stream stream, int statusCode, string body, CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes(body);
        var reason = statusCode switch
        {
            200 => "OK",
            400 => "Bad Request",
            404 => "Not Found",
            _ => "Error"
        };
        var header = $"HTTP/1.1 {statusCode} {reason}\r\nContent-Type: application/json; charset=utf-8\r\n" +
                     $"Content-Length: {payload.Length}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
