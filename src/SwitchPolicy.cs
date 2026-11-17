using LogiSwitch.Audio;
using LogiSwitch.Hid;
using Timer = System.Windows.Forms.Timer;

namespace LogiSwitch;

/// <summary>
/// Decides when to move the Windows default devices. Runs on the UI thread.
///
/// Off: after <see cref="Config.OffDelayMs"/>, switch to the fallback devices.
/// On:  switch back to the headset right away.
/// With <see cref="Config.RespectManualChoice"/>, a switch only happens when the current default
/// is the device we expect (the headset when turning off, the fallback when turning on), so a
/// device the user picked by hand is left alone.
/// </summary>
internal sealed class SwitchPolicy : IDisposable
{
    private readonly Config _config;
    private readonly Timer _offTimer = new();
    private readonly Dictionary<EDataFlow, string> _switchedTo = new();

    public SwitchPolicy(Config config)
    {
        _config = config;
        _offTimer.Tick += (_, _) =>
        {
            _offTimer.Stop();
            SwitchToFallback();
        };
    }

    public void OnStateChanged(HeadsetState state)
    {
        _offTimer.Stop();
        if (_config.Paused)
        {
            Log.Info($"Paused; ignoring {state}");
            return;
        }

        switch (state)
        {
            case HeadsetState.Off:
                _offTimer.Interval = Math.Max(1, _config.OffDelayMs);
                _offTimer.Start();
                break;
            case HeadsetState.On:
                SwitchToHeadset();
                break;
        }
        // ReceiverMissing: Windows removes the endpoints itself and picks another default.
    }

    public void SwitchToFallback()
    {
        foreach (var flow in Flows())
        {
            try
            {
                var devices = AudioEndpoints.ListActive(flow);
                var headset = FindHeadset(devices);
                if (headset is null)
                {
                    Log.Info($"{flow}: headset endpoint not active, nothing to switch");
                    continue;
                }

                var current = AudioEndpoints.GetDefaultId(flow);
                if (_config.RespectManualChoice && current != headset.Id)
                {
                    Log.Info($"{flow}: default is {NameOf(devices, current)}, not the headset; leaving it");
                    continue;
                }

                var fallback = ResolveFallback(flow, devices);
                if (fallback is null)
                {
                    Log.Warn($"{flow}: no fallback device available");
                    continue;
                }

                AudioEndpoints.SetDefault(fallback.Id);
                _switchedTo[flow] = fallback.Id;
                Log.Info($"{flow}: switched to {fallback.Name}");
            }
            catch (Exception ex)
            {
                Log.Error($"{flow}: switch to fallback failed", ex);
            }
        }
    }

    public void SwitchToHeadset()
    {
        foreach (var flow in Flows())
        {
            try
            {
                var devices = AudioEndpoints.ListActive(flow);
                var headset = FindHeadset(devices);
                if (headset is null)
                {
                    Log.Info($"{flow}: headset endpoint not active, nothing to switch");
                    continue;
                }

                var current = AudioEndpoints.GetDefaultId(flow);
                if (current == headset.Id)
                {
                    continue;
                }

                var fallback = ResolveFallback(flow, devices);
                var expected = current is not null
                    && (current == fallback?.Id || (_switchedTo.TryGetValue(flow, out var ours) && current == ours));
                if (_config.RespectManualChoice && current is not null && !expected)
                {
                    Log.Info($"{flow}: default is {NameOf(devices, current)}, not the fallback; leaving it");
                    continue;
                }

                AudioEndpoints.SetDefault(headset.Id);
                _switchedTo.Remove(flow);
                Log.Info($"{flow}: switched to {headset.Name}");
            }
            catch (Exception ex)
            {
                Log.Error($"{flow}: switch to headset failed", ex);
            }
        }
    }

    /// <summary>The device the policy would fall back to for this flow, or null.</summary>
    public AudioDevice? ResolveFallback(EDataFlow flow, List<AudioDevice> devices)
    {
        var candidates = devices.Where(d => !IsHeadset(d)).ToList();
        if (flow == EDataFlow.Render)
        {
            return candidates.FirstOrDefault(d => d.Id == _config.FallbackOutputId)
                ?? candidates.FirstOrDefault(d =>
                    !string.IsNullOrWhiteSpace(_config.FallbackOutputNameMatch)
                    && d.Name.Contains(_config.FallbackOutputNameMatch, StringComparison.OrdinalIgnoreCase));
        }

        return candidates.FirstOrDefault(d => d.Id == _config.FallbackInputId)
            ?? (_config.FallbackInputId is null ? candidates.FirstOrDefault() : null);
    }

    public bool IsHeadset(AudioDevice device) =>
        device.Name.Contains(_config.HeadsetNameMatch, StringComparison.OrdinalIgnoreCase);

    private AudioDevice? FindHeadset(List<AudioDevice> devices) => devices.FirstOrDefault(IsHeadset);

    private IEnumerable<EDataFlow> Flows()
    {
        yield return EDataFlow.Render;
        if (_config.SwitchInput)
        {
            yield return EDataFlow.Capture;
        }
    }

    private static string NameOf(List<AudioDevice> devices, string? id) =>
        id is null ? "(none)" : devices.FirstOrDefault(d => d.Id == id)?.Name ?? id;

    public void Dispose() => _offTimer.Dispose();
}
