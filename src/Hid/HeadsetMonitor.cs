using System.Collections.Concurrent;

namespace LogiSwitch.Hid;

public enum HeadsetState
{
    Unknown,
    ReceiverMissing,
    Off,
    On,
}

/// <summary>
/// Tracks whether the G522 headset is powered on, by talking to its LIGHTSPEED receiver.
///
/// The receiver speaks Logitech's "Centurion" protocol on HID usage page 0xFFA0 with 64-byte
/// frames: <c>50 23 | len | flags | feature-index | function|sw-id | params...</c>, where
/// <c>len</c> counts the flags byte plus the payload. Its bridge feature (ID 0x0003):
/// <list type="bullet">
///   <item>sends an unsolicited event <c>[bridge, 00, 00, connected]</c> on headset power on/off;</item>
///   <item>answers function 0 with <c>03 E8 00 nn 01</c>, where nn is the number of connected headsets.</item>
/// </list>
/// Both were verified on hardware; the format matches HeadsetControl's G522 support.
/// The receiver is shared with G HUB, so requests carry our own software ID.
/// </summary>
public sealed class HeadsetMonitor : IDisposable
{
    private const ushort VendorId = 0x046D;
    private const ushort ProductId = 0x0B18;          // G522 LIGHTSPEED receiver
    private const ushort VendorUsagePage = 0xFFA0;
    private const int FrameSize = 64;
    private const byte SoftwareId = 0x0B;             // G HUB uses 0x0F, HeadsetControl 0x01
    private const byte RootFeatureIndex = 0x00;
    private const ushort BridgeFeatureId = 0x0003;
    private static readonly byte[] Prefix = [0x50, 0x23];
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(3);

    private readonly TimeSpan _pollInterval;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<byte[]>> _pending = new();
    private readonly SemaphoreSlim _refreshSignal = new(0, 1);
    private FileStream? _writer;
    private byte? _bridgeIndex;
    private Task? _loop;
    private HeadsetState _state = HeadsetState.Unknown;

    public HeadsetMonitor(TimeSpan pollInterval) => _pollInterval = pollInterval;

    /// <summary>Raised on a background thread whenever the state changes.</summary>
    public event Action<HeadsetState>? StateChanged;

    public HeadsetState State => _state;

    public void Start() => _loop ??= Task.Run(() => RunAsync(_cts.Token));

    /// <summary>Ask the receiver for the current state soon (e.g. after resume from sleep).</summary>
    public void RequestRefresh()
    {
        try { _refreshSignal.Release(); }
        catch (SemaphoreFullException) { /* a refresh is already queued */ }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        string? lastMissingReason = null;
        while (!ct.IsCancellationRequested)
        {
            var path = FindReceiverPath();
            if (path is null)
            {
                if (lastMissingReason != "absent")
                {
                    Log.Info("G522 receiver not found; waiting for it to be plugged in");
                    lastMissingReason = "absent";
                }
                SetState(HeadsetState.ReceiverMissing);
                await DelayQuietly(ReconnectDelay, ct);
                continue;
            }

            // HID handles aren't seekable, so RandomAccess can't be used, and one FileStream shouldn't
            // carry a read and a write at the same time. Use a separate handle for each direction.
            var readHandle = HidNative.Open(path);
            var writeHandle = readHandle is null ? null : HidNative.Open(path);
            if (readHandle is null || writeHandle is null)
            {
                readHandle?.Dispose();
                Log.Warn($"Could not open receiver at {path}");
                await DelayQuietly(ReconnectDelay, ct);
                continue;
            }

            var readStream = new FileStream(readHandle, FileAccess.Read, 0, isAsync: true);
            var writeStream = new FileStream(writeHandle, FileAccess.Write, 0, isAsync: true);
            lastMissingReason = null;
            _writer = writeStream;
            _bridgeIndex = null;
            Log.Info("Receiver opened");

            var reader = Task.Run(() => ReadLoopAsync(readStream, ct), ct);
            using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try
            {
                var lookup = await SendRequestAsync(RootFeatureIndex, 0x00, [(byte)(BridgeFeatureId >> 8), (byte)BridgeFeatureId]);
                if (lookup.Length == 0 || lookup[0] == 0)
                {
                    throw new InvalidOperationException("Receiver has no bridge feature");
                }
                _bridgeIndex = lookup[0];
                Log.Info($"Bridge feature at index 0x{_bridgeIndex:X2}");

                await RefreshStatusAsync();

                // Keep one outstanding wait on the refresh signal; a fresh wait per loop
                // would leave orphaned waiters that swallow later requests.
                var refresh = _refreshSignal.WaitAsync(connectionCts.Token);
                while (!reader.IsCompleted)
                {
                    var poll = Task.Delay(_pollInterval, connectionCts.Token);
                    var done = await Task.WhenAny(reader, poll, refresh);
                    if (done == reader)
                    {
                        break;
                    }
                    if (done == refresh)
                    {
                        Log.Info("Refresh requested");
                        refresh = _refreshSignal.WaitAsync(connectionCts.Token);
                    }
                    await RefreshStatusAsync();
                }

                await reader;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error("Receiver connection lost", ex);
            }
            finally
            {
                connectionCts.Cancel();
                _writer = null;
                writeStream.Dispose();
                readStream.Dispose();
                FailPending();
                try { await reader; } catch { /* already logged or expected after dispose */ }
            }

            SetState(HeadsetState.ReceiverMissing);
            await DelayQuietly(TimeSpan.FromSeconds(1), ct);
        }
    }

    private static string? FindReceiverPath()
    {
        var idFragment = $"vid_{VendorId:x4}&pid_{ProductId:x4}";
        foreach (var path in HidNative.EnumerateHidPaths())
        {
            if (!path.Contains(idFragment, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var handle = HidNative.Open(path);
            if (handle is null)
            {
                continue;
            }

            var caps = HidNative.GetCaps(handle);
            if (caps is { } c && c.UsagePage == VendorUsagePage && c.InputReportByteLength == FrameSize)
            {
                return path;
            }
        }
        return null;
    }

    private async Task ReadLoopAsync(FileStream stream, CancellationToken ct)
    {
        var buffer = new byte[FrameSize];
        while (!ct.IsCancellationRequested)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read <= 0)
            {
                throw new IOException("Receiver read returned no data");
            }
            HandleFrame(buffer.AsSpan(0, read));
        }
    }

    private void HandleFrame(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 6 || frame[0] != Prefix[0] || frame[1] != Prefix[1])
        {
            return;
        }

        int length = frame[2];                       // flags byte + payload
        if (length < 3 || 3 + length > frame.Length)
        {
            return;
        }
        var payload = frame.Slice(4, length - 1);

        // Unsolicited bridge connection event: [bridge, 00, 00, connected].
        if (_bridgeIndex is { } bridge && payload.Length >= 4 && payload[0] == bridge && payload[1] == 0x00)
        {
            var on = payload[3] != 0;
            Log.Info($"Receiver event: headset {(on ? "connected" : "disconnected")}");
            SetState(on ? HeadsetState.On : HeadsetState.Off);
            return;
        }

        // Error reply: [FF, feature, function|sw, error code].
        if (payload.Length >= 3 && payload[0] == 0xFF && (payload[2] & 0x0F) == SoftwareId)
        {
            if (_pending.TryRemove(Key(payload[1], payload[2]), out var failed))
            {
                failed.TrySetException(new IOException($"Receiver rejected request (error 0x{(payload.Length > 3 ? payload[3] : 0):X2})"));
            }
            return;
        }

        // Reply to one of our requests.
        if (payload.Length >= 2 && (payload[1] & 0x0F) == SoftwareId
            && _pending.TryRemove(Key(payload[0], payload[1]), out var waiter))
        {
            waiter.TrySetResult(payload[2..].ToArray());
        }
    }

    private async Task RefreshStatusAsync()
    {
        if (_bridgeIndex is not { } bridge)
        {
            return;
        }
        var status = await SendRequestAsync(bridge, 0x00, []);
        if (status.Length < 4)
        {
            Log.Warn($"Unexpected bridge status reply: {Convert.ToHexString(status)}");
            return;
        }
        SetState(status[2] == 0 && status[3] == 0 ? HeadsetState.Off : HeadsetState.On);
    }

    private async Task<byte[]> SendRequestAsync(byte featureIndex, byte function, byte[] parameters)
    {
        var writer = _writer ?? throw new IOException("Receiver not open");
        await _requestGate.WaitAsync();
        try
        {
            var functionSw = (byte)((function & 0xF0) | SoftwareId);
            var frame = new byte[FrameSize];
            frame[0] = Prefix[0];
            frame[1] = Prefix[1];
            frame[2] = (byte)(2 + parameters.Length + 1);
            frame[3] = 0x00;
            frame[4] = featureIndex;
            frame[5] = functionSw;
            parameters.CopyTo(frame, 6);

            var key = Key(featureIndex, functionSw);
            var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[key] = tcs;
            try
            {
                await writer.WriteAsync(frame);
                return await tcs.Task.WaitAsync(RequestTimeout);
            }
            finally
            {
                _pending.TryRemove(key, out _);
            }
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private static int Key(byte featureIndex, byte functionSw) => (featureIndex << 8) | functionSw;

    private void FailPending()
    {
        foreach (var key in _pending.Keys)
        {
            if (_pending.TryRemove(key, out var tcs))
            {
                tcs.TrySetException(new IOException("Receiver closed"));
            }
        }
    }

    private void SetState(HeadsetState state)
    {
        if (_state == state)
        {
            return;
        }
        Log.Info($"Headset state: {_state} -> {state}");
        _state = state;
        try
        {
            StateChanged?.Invoke(state);
        }
        catch (Exception ex)
        {
            Log.Error("StateChanged handler failed", ex);
        }
    }

    private static async Task DelayQuietly(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _writer?.Dispose();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts.Dispose();
    }
}
