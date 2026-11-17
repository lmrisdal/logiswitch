namespace LogiSwitch;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args is ["--preview-icons", var folder])
        {
            PreviewIcons(folder);
            return;
        }

        using var mutex = new Mutex(initiallyOwned: true, @"Local\logiswitch", out var isFirstInstance);
        if (!isFirstInstance)
        {
            return;
        }

        ApplicationConfiguration.Initialize();
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        Application.ThreadException += (_, e) => Log.Error("Unhandled UI exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("Unhandled exception", e.ExceptionObject as Exception);

        Application.Run(new TrayApp());
    }

    /// <summary>Developer aid: writes every tray icon variant to PNG files.</summary>
    private static void PreviewIcons(string folder)
    {
        Directory.CreateDirectory(folder);
        foreach (var size in new[] { 16, 20, 24, 32 })
        {
            using var sized = new TrayIcons { IconSize = size };
            foreach (var style in Enum.GetValues<TrayIconStyle>())
            {
                foreach (var (name, percent, charging) in new[] { ("100", 100, false), ("67", 67, false), ("8", 8, false), ("54c", 54, true) })
                {
                    using var bitmap = sized.For(Hid.HeadsetState.On, false, new Hid.BatteryStatus(percent, charging), style).ToBitmap();
                    bitmap.Save(Path.Combine(folder, $"{style}{size}-{name}.png"));
                }
            }
        }

        using var icons = new TrayIcons();
        var batteries = new (string Name, Hid.BatteryStatus? Battery)[]
        {
            ("none", null), ("100", new(100, false)), ("67", new(67, false)), ("25", new(25, false)),
            ("8", new(8, false)), ("54-charging", new(54, true)),
        };
        foreach (var style in Enum.GetValues<TrayIconStyle>())
        {
            foreach (var (name, battery) in batteries)
            {
                var state = battery is null ? Hid.HeadsetState.Off : Hid.HeadsetState.On;
                using var bitmap = icons.For(state, paused: false, battery, style).ToBitmap();
                bitmap.Save(Path.Combine(folder, $"{style}-{name}.png"));
            }
        }
    }
}
