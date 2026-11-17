using System.Diagnostics;
using LogiSwitch.Audio;
using LogiSwitch.Hid;
using Microsoft.Win32;

namespace LogiSwitch;

internal sealed class TrayApp : ApplicationContext
{
    private readonly Config _config = Config.Load();
    private readonly SynchronizationContext _ui;
    private readonly HeadsetMonitor _monitor;
    private readonly SwitchPolicy _policy;
    private readonly TrayIcons _icons = new();
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu = new();
    private HeadsetState _state = HeadsetState.Unknown;

    public TrayApp()
    {
        _ui = SynchronizationContext.Current
            ?? throw new InvalidOperationException("TrayApp must be created on the UI thread");
        _policy = new SwitchPolicy(_config);
        _monitor = new HeadsetMonitor(TimeSpan.FromSeconds(Math.Max(10, _config.StatusPollSeconds)));

        _menu.Opening += (_, _) => BuildMenu();
        _tray = new NotifyIcon
        {
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                _monitor.RequestRefresh();
            }
        };
        UpdateTray();

        _monitor.StateChanged += state => _ui.Post(_ => OnStateChanged(state), null);
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        Log.Info($"logiswitch {Application.ProductVersion} started; config {Config.FilePath}");
        _monitor.Start();
    }

    private void OnStateChanged(HeadsetState state)
    {
        _state = state;
        UpdateTray();
        _policy.OnStateChanged(state);
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            Log.Info("Resumed from sleep");
            _monitor.RequestRefresh();
        }
    }

    private void UpdateTray()
    {
        _tray.Icon = _icons.For(_state, _config.Paused);
        var text = $"logiswitch: {StateText(_state)}{(_config.Paused ? " (paused)" : "")}";
        _tray.Text = text.Length > 127 ? text[..127] : text;
    }

    private static string StateText(HeadsetState state) => state switch
    {
        HeadsetState.On => "headset on",
        HeadsetState.Off => "headset off",
        HeadsetState.ReceiverMissing => "receiver not found",
        _ => "starting…",
    };

    private void BuildMenu()
    {
        _menu.Items.Clear();
        _menu.Items.Add(new ToolStripMenuItem($"Headset: {StateText(_state)}") { Enabled = false });
        _menu.Items.Add(new ToolStripSeparator());

        List<AudioDevice> outputs = [], inputs = [];
        try
        {
            outputs = AudioEndpoints.ListActive(EDataFlow.Render);
            inputs = AudioEndpoints.ListActive(EDataFlow.Capture);
        }
        catch (Exception ex)
        {
            Log.Error("Could not list audio devices", ex);
        }

        // Fallback output
        var outputMenu = new ToolStripMenuItem("Fallback output");
        var currentOutput = _policy.ResolveFallback(EDataFlow.Render, outputs);
        foreach (var device in outputs.Where(d => !_policy.IsHeadset(d)))
        {
            var item = new ToolStripMenuItem(device.Name) { Checked = device.Id == currentOutput?.Id };
            item.Click += (_, _) =>
            {
                _config.FallbackOutputId = device.Id;
                _config.Save();
                Log.Info($"Fallback output set to {device.Name}");
            };
            outputMenu.DropDownItems.Add(item);
        }
        if (outputMenu.DropDownItems.Count == 0)
        {
            outputMenu.DropDownItems.Add(new ToolStripMenuItem("(no other outputs)") { Enabled = false });
        }
        _menu.Items.Add(outputMenu);

        // Fallback microphone
        var inputMenu = new ToolStripMenuItem("Fallback microphone");
        var dontSwitch = new ToolStripMenuItem("Don't switch microphone") { Checked = !_config.SwitchInput };
        dontSwitch.Click += (_, _) => { _config.SwitchInput = false; _config.Save(); };
        var auto = new ToolStripMenuItem("Automatic (first available)")
        {
            Checked = _config.SwitchInput && _config.FallbackInputId is null,
        };
        auto.Click += (_, _) => { _config.SwitchInput = true; _config.FallbackInputId = null; _config.Save(); };
        inputMenu.DropDownItems.Add(dontSwitch);
        inputMenu.DropDownItems.Add(auto);
        inputMenu.DropDownItems.Add(new ToolStripSeparator());
        foreach (var device in inputs.Where(d => !_policy.IsHeadset(d)))
        {
            var item = new ToolStripMenuItem(device.Name)
            {
                Checked = _config.SwitchInput && device.Id == _config.FallbackInputId,
            };
            item.Click += (_, _) =>
            {
                _config.SwitchInput = true;
                _config.FallbackInputId = device.Id;
                _config.Save();
                Log.Info($"Fallback microphone set to {device.Name}");
            };
            inputMenu.DropDownItems.Add(item);
        }
        _menu.Items.Add(inputMenu);
        _menu.Items.Add(new ToolStripSeparator());

        var pause = new ToolStripMenuItem("Pause switching") { Checked = _config.Paused };
        pause.Click += (_, _) =>
        {
            _config.Paused = !_config.Paused;
            _config.Save();
            Log.Info(_config.Paused ? "Paused" : "Resumed");
            UpdateTray();
        };
        _menu.Items.Add(pause);

        var autostart = new ToolStripMenuItem("Start with Windows") { Checked = SafeAutostartEnabled() };
        autostart.Click += (_, _) =>
        {
            try { Autostart.Set(!autostart.Checked); }
            catch (Exception ex) { Log.Error("Could not change autostart", ex); }
        };
        _menu.Items.Add(autostart);

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Open log", null, (_, _) => OpenFile(Log.FilePath));
        _menu.Items.Add("Open config", null, (_, _) => OpenFile(Config.FilePath));
        _menu.Items.Add("Exit", null, (_, _) => ExitThread());
    }

    private static bool SafeAutostartEnabled()
    {
        try { return Autostart.IsEnabled; }
        catch { return false; }
    }

    private static void OpenFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Could not open {path}", ex);
        }
    }

    protected override void ExitThreadCore()
    {
        Log.Info("Exiting");
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _tray.Visible = false;
        _monitor.Dispose();
        _policy.Dispose();
        _tray.Dispose();
        _menu.Dispose();
        _icons.Dispose();
        base.ExitThreadCore();
    }
}
