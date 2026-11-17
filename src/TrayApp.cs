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
    private BatteryStatus? _battery;

    public TrayApp()
    {
        _ui = SynchronizationContext.Current
            ?? throw new InvalidOperationException("TrayApp must be created on the UI thread");
        _policy = new SwitchPolicy(_config);
        _monitor = new HeadsetMonitor(TimeSpan.FromSeconds(Math.Max(10, _config.StatusPollSeconds)));

        _menu.Opening += (_, _) =>
        {
            BuildMenu();
            _monitor.RequestRefresh();
        };
        _tray = new NotifyIcon
        {
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _tray.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ShowMenuFromTray();
            }
        };
        UpdateTray();

        _monitor.StateChanged += state => _ui.Post(_ => OnStateChanged(state), null);
        _monitor.BatteryChanged += battery => _ui.Post(_ =>
        {
            _battery = battery;
            UpdateTray();
        }, null);
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

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

    /// <summary>
    /// Opens the menu the way a right-click does. NotifyIcon's own (non-public) ShowContextMenu
    /// also makes the menu close properly when you click elsewhere; Show() alone doesn't.
    /// </summary>
    private void ShowMenuFromTray()
    {
        var show = typeof(NotifyIcon).GetMethod("ShowContextMenu",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (show is not null)
        {
            show.Invoke(_tray, null);
        }
        else
        {
            _menu.Show(Cursor.Position);
        }
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        // Light/dark mode changes arrive as General; redraw so the glyph matches the taskbar.
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
        {
            _ui.Post(_ => UpdateTray(), null);
        }
    }

    private void UpdateTray()
    {
        _icons.LightTaskbar = Ui.Theme.Current.IsLight;
        _tray.Icon = _icons.For(_state, _config.Paused, _battery, _config.TrayIconStyle);
        var text = $"logiswitch: {StateText(_state)}{BatteryText(_battery)}{(_config.Paused ? " (paused)" : "")}";
        _tray.Text = text.Length > 127 ? text[..127] : text;
    }

    private static string StateText(HeadsetState state) => state switch
    {
        HeadsetState.On => "headset on",
        HeadsetState.Off => "headset off",
        HeadsetState.ReceiverMissing => "receiver not found",
        _ => "starting…",
    };

    private static string BatteryText(BatteryStatus? battery) => battery is null
        ? ""
        : $", battery {battery.Percent}%{(battery.Charging ? " (charging)" : "")}";

    private void BuildMenu()
    {
        _menu.Items.Clear();
        _menu.Items.Add(new ToolStripMenuItem($"Headset: {StateText(_state)}{BatteryText(_battery)}") { Enabled = false });
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

        var iconMenu = new ToolStripMenuItem("Tray icon");
        foreach (var (style, label) in new[]
                 {
                     (TrayIconStyle.Percent, "Battery percentage"),
                     (TrayIconStyle.Bar, "Headphones with battery bar"),
                 })
        {
            var item = new ToolStripMenuItem(label) { Checked = _config.TrayIconStyle == style };
            item.Click += (_, _) =>
            {
                _config.TrayIconStyle = style;
                _config.Save();
                UpdateTray();
            };
            iconMenu.DropDownItems.Add(item);
        }
        _menu.Items.Add(iconMenu);

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

        Ui.MenuRenderer.Apply(_menu, Ui.Theme.Current);
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
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _tray.Visible = false;
        _monitor.Dispose();
        _policy.Dispose();
        _tray.Dispose();
        _menu.Dispose();
        _icons.Dispose();
        base.ExitThreadCore();
    }
}
