using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using LogiSwitch.Hid;

namespace LogiSwitch;

/// <summary>Draws a small headphones glyph, coloured by headset state.</summary>
internal sealed class TrayIcons : IDisposable
{
    private readonly Dictionary<HeadsetState, Icon> _icons = new();

    public Icon For(HeadsetState state, bool paused)
    {
        var key = paused ? HeadsetState.Unknown : state;
        if (!_icons.TryGetValue(key, out var icon))
        {
            icon = Draw(ColorFor(key));
            _icons[key] = icon;
        }
        return icon;
    }

    private static Color ColorFor(HeadsetState state) => state switch
    {
        HeadsetState.On => Color.FromArgb(0x3D, 0xDC, 0x84),
        HeadsetState.Off => Color.FromArgb(0x9A, 0x9A, 0x9A),
        HeadsetState.ReceiverMissing => Color.FromArgb(0xF0, 0xA0, 0x30),
        _ => Color.FromArgb(0x60, 0x60, 0x60),
    };

    private static Icon Draw(Color color)
    {
        const int size = 32;
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var pen = new Pen(color, 4f);
            g.DrawArc(pen, 5, 4, 22, 24, 180, 180);       // headband
            using var brush = new SolidBrush(color);
            g.FillRectangle(brush, 3, 15, 8, 14);          // left ear cup
            g.FillRectangle(brush, 21, 15, 8, 14);         // right ear cup
        }

        var handle = bitmap.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(handle).Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        foreach (var icon in _icons.Values)
        {
            icon.Dispose();
        }
        _icons.Clear();
    }
}
