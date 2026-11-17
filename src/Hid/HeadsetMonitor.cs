using System.Collections.Concurrent;

namespace LogiSwitch.Hid;

public enum HeadsetState
{
    Unknown,
    ReceiverMissing,
    Off,
    On,
}

public sealed record BatteryStatus(int Percent, bool Charging);

/// <summary>
/// Tracks whether the G522 headset is powered on, and its battery, by talking to its LIGHTSPEED receiver.
///
/// The receiver speaks Logitech's "Centurion" protocol on HID usage page 0xFFA0 with 64-byte
/// frames: <c>50 23 | len | flags | feature-index | function|sw-id | params...</c>, where
/// <c>len</c> counts the flags byte plus the payload. Its bridge feature (ID 0x0003):
/// <list type="bullet">
///   <item>sends an unsolicited event <c>[bridge, 00, 00, connected]</c> on headset power on/off;</item>
///   <item>answers function 0 with <c>03 E8 00 nn 01</c>, where nn is the number of connected headsets;</item>
///   <item>forwards messages to the headset with function 1:
///     <c>[bridge, 1|sw, size-hi, size-lo, 00, sub-feature, function|sw, params...]</c>. The headset's
///     reply comes back as <c>[bridge, 10, size-hi, size-lo, 00, sub-feature, function|sw, result...]</c>.</item>
/// </list>
/// The headset's battery feature (ID 0x0104) answers function 0 with <c>[percent, ?, charging-state]</c>.
/// All of this was verified on hardware; the format matches HeadsetControl's G522 support.
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
    private const ushort BatteryFeatureId = 0x0104;
    private const byte BridgeSendFunction = 0x10;
    private static readonly TimeSpan BatteryDelayAfterConnect = TimeSpan.FromSeconds(1.5);
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
    private byte? _batteryIndex;
    private bool _batteryUnsupported;
    private Task? _loop;
    private HeadsetState _state = HeadsetState.Unknown;
    private BatteryStatus? _battery;

    public HeadsetMonitor(TimeSpan pollInterval) => _pollInterval = pollInterval;

    /// <summary>Raised on a background thread whenever the state changes.</summary>
    public event Action<HeadsetState>? StateChanged;

    /// <summary>Raised on a background thread when the battery reading changes (null = unknown).</summary>
    public event Action<BatteryStatus?>? BatteryChanged;

    public HeadsetState State => _state;

    public BatteryStatus? Battery => _battery;

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
            _batteryIndex = null;
            _batteryUnsupported = false;
            Log.Info("Receiver opened");

            var reader = Task.Run(() => ReadLoopAsync(readStream, ct), ct);
            using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try
            {
                var lookup = await SendRequestAsync(RootFeatureIndex, 0x00, [(byte)(BridgeFeatureId >> 8), (byte)(BridgeFeatureId & 0xFF)]);
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
            SetBattery(null);
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
            if (on)
            {
                // Give the headset a moment to settle before asking it for its battery level.
                _ = Task.Delay(BatteryDelayAfterConnect).ContinueWith(_ => RequestRefresh(), TaskScheduler.Default);
            }
            else
            {
                SetBattery(null);
            }
            return;
        }

        // Message from the headset, relayed by the bridge: [bridge, 1x, size, size, 00, sub...].
        if (_bridgeIndex is { } relay && payload.Length >= 2 && payload[0] == relay
            && (payload[1] & 0xF0) == BridgeSendFunction)
        {
            HandleBridgeMessage(payload);
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

    private void HandleBridgeMessage(ReadOnlySpan<byte> payload)
    {
        // Low nibble set = the bridge acknowledging a request (ours or G HUB's); the real
        // answer follows as a separate message with software ID 0.
        if ((payload[1] & 0x0F) != 0 || payload.Length < 7 || payload[4] != 0x00)
        {
            return;
        }

        var sub = payload[5..];                      // [sub-feature, function|sw, result...]
        if (sub[0] == 0xFF)
        {
            // Headset error: [FF, sub-feature, function|sw, code].
            if (sub.Length >= 3 && (sub[2] & 0x0F) == SoftwareId
                && _pending.TryRemove(BridgeKey(sub[1], sub[2]), out var failed))
            {
                failed.TrySetException(new IOException($"Headset rejected request (error 0x{(sub.Length > 3 ? sub[3] : 0):X2})"));
            }
            return;
        }

        if ((sub[1] & 0x0F) == SoftwareId)
        {
            if (_pending.TryRemove(BridgeKey(sub[0], sub[1]), out var waiter))
            {
                waiter.TrySetResult(sub[2..].ToArray());
            }
            return;
        }

        // Software ID 0 = unsolicited notification from the headset.
        if ((sub[1] & 0x0F) == 0 && _batteryIndex is { } battery && sub[0] == battery)
        {
            Log.Info($"Battery notification: {Convert.ToHexString(sub)}");
            if (ParseBattery(sub[2..]) is { } status)
            {
                SetBattery(status);
            }
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
        var on = !(status[2] == 0 && status[3] == 0);
        SetState(on ? HeadsetState.On : HeadsetState.Off);

        if (!on)
        {
            SetBattery(null);
            return;
        }

        try
        {
            await RefreshBatteryAsync();
        }
        catch (Exception ex)
        {
            // Not fatal: the headset may still be settling after power-on. The next poll retries.
            Log.Warn($"Battery read failed: {ex.Message}");
        }
    }

    private async Task RefreshBatteryAsync()
    {
        if (_batteryUnsupported)
        {
            return;
        }

        if (_batteryIndex is null)
        {
            var lookup = await SendBridgeRequestAsync(RootFeatureIndex, 0x00, [(byte)(BatteryFeatureId >> 8), (byte)(BatteryFeatureId & 0xFF)]);
            if (lookup.Length == 0 || lookup[0] == 0)
            {
                Log.Warn("Headset has no battery feature");
                _batteryUnsupported = true;
                return;
            }
            _batteryIndex = lookup[0];
            Log.Info($"Battery feature at headset index 0x{_batteryIndex:X2}");
        }

        var reply = await SendBridgeRequestAsync(_batteryIndex.Value, 0x00, []);
        if (ParseBattery(reply) is { } status)
        {
            SetBattery(status);
        }
        else
        {
            Log.Warn($"Unexpected battery reply: {Convert.ToHexString(reply)}");
        }
    }

    private static BatteryStatus? ParseBattery(ReadOnlySpan<byte> result)
    {
        if (result.Length == 0 || result[0] > 100)
        {
            return null;
        }
        // Charging states 1 and 2 both mean charging (per HeadsetControl).
        var charging = result.Length >= 3 && result[2] is 1 or 2;
        return new BatteryStatus(result[0], charging);
    }

    private Task<byte[]> SendRequestAsync(byte featureIndex, byte function, byte[] parameters)
    {
        var functionSw = (byte)((function & 0xF0) | SoftwareId);
        byte[] payload = [featureIndex, functionSw, .. parameters];
        return SendAsync(payload, Key(featureIndex, functionSw));
    }

    /// <summary>Sends a request to the headset through the receiver's bridge.</summary>
    private Task<byte[]> SendBridgeRequestAsync(byte subFeatureIndex, byte function, byte[] parameters)
    {
        var bridge = _bridgeIndex ?? throw new IOException("Bridge not discovered");
        var functionSw = (byte)((function & 0xF0) | SoftwareId);
        byte[] sub = [0x00, subFeatureIndex, functionSw, .. parameters];
        byte[] payload =
        [
            bridge, (byte)(BridgeSendFunction | SoftwareId),
            (byte)((sub.Length >> 8) & 0x0F), (byte)sub.Length,
            .. sub,
        ];
        return SendAsync(payload, BridgeKey(subFeatureIndex, functionSw));
    }

    private async Task<byte[]> SendAsync(byte[] payload, int key)
    {
        var writer = _writer ?? throw new IOException("Receiver not open");
        await _requestGate.WaitAsync();
        try
        {
            var frame = new byte[FrameSize];
            frame[0] = Prefix[0];
            frame[1] = Prefix[1];
            frame[2] = (byte)(payload.Length + 1);
            frame[3] = 0x00;
            payload.CopyTo(frame, 4);

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

    private static int BridgeKey(byte subFeatureIndex, byte functionSw) => 0x10000 | Key(subFeatureIndex, functionSw);

    private void SetBattery(BatteryStatus? battery)
    {
        if (_battery == battery)
        {
            return;
        }
        Log.Info(battery is null
            ? "Battery: unknown"
            : $"Battery: {battery.Percent}%{(battery.Charging ? " (charging)" : "")}");
        _battery = battery;
        try
        {
            BatteryChanged?.Invoke(battery);
        }
        catch (Exception ex)
        {
            Log.Error("BatteryChanged handler failed", ex);
        }
    }

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
