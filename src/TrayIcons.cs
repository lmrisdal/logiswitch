using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using LogiSwitch.Hid;

namespace LogiSwitch;

/// <summary>
/// Draws a small headphones glyph, coloured by headset state, with a battery bar underneath
/// when the battery level is known.
/// </summary>
internal sealed class TrayIcons : IDisposable
{
    private static readonly Color BatteryGood = Color.FromArgb(0x3D, 0xDC, 0x84);
    private static readonly Color BatteryLow = Color.FromArgb(0xF0, 0xA0, 0x30);
    private static readonly Color BatteryCritical = Color.FromArgb(0xE8, 0x48, 0x48);
    private static readonly Color BatteryCharging = Color.FromArgb(0x4A, 0xA3, 0xFF);

    private readonly Dictionary<(TrayIconStyle Style, HeadsetState State, int Level, bool Charging), Icon> _icons = new();
    private readonly FontFamily _digitFont = PickDigitFont();

    public Icon For(HeadsetState state, bool paused, BatteryStatus? battery, TrayIconStyle style)
    {
        var shownState = paused ? HeadsetState.Unknown : state;
        var level = battery switch
        {
            null => -1,
            // The bar only has a few pixels to show, so 10% steps are plenty.
            _ when style == TrayIconStyle.Bar => (int)Math.Round(battery.Percent / 10.0) * 10,
            _ => battery.Percent,
        };
        var key = (style, shownState, level, battery?.Charging ?? false);
        if (!_icons.TryGetValue(key, out var icon))
        {
            var shown = battery is null ? null : battery with { Percent = level };
            icon = style == TrayIconStyle.Percent && shown is not null
                ? DrawPercent(shownState, shown, paused)
                : Draw(ColorFor(shownState), shown);
            _icons[key] = icon;
        }
        return icon;
    }

    /// <summary>Narrow, bold digits read best at tray size; fall back to Segoe UI.</summary>
    private static FontFamily PickDigitFont()
    {
        foreach (var name in new[] { "Bahnschrift SemiBold Condensed", "Bahnschrift", "Segoe UI" })
        {
            try
            {
                return new FontFamily(name);
            }
            catch (ArgumentException)
            {
                // Not installed; try the next one.
            }
        }
        return FontFamily.GenericSansSerif;
    }

    /// <summary>
    /// Headphones on top, the percentage underneath. Drawn at the tray's real icon size so the
    /// text isn't blurred by scaling. The "%" sign is dropped at small sizes.
    /// </summary>
    private Icon DrawPercent(HeadsetState state, BatteryStatus battery, bool paused)
    {
        var size = IconSize;
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            var glyphHeight = size * 0.46f;
            DrawHeadphones(g, ColorFor(state), new RectangleF((size - glyphHeight) / 2f, 0, glyphHeight, glyphHeight));

            var textArea = new RectangleF(0, size * 0.52f, size, size * 0.48f);
            // Below 24 px (100% / 125% display scaling) "100%" turns to mush, so drop the sign there.
            using var path = FitText(size >= 24 ? $"{battery.Percent}%" : $"{battery.Percent}", textArea);

            using var brush = new SolidBrush(paused ? ColorFor(HeadsetState.Unknown) : BatteryColor(battery));
            g.FillPath(brush, path);
        }

        return ToIcon(bitmap);
    }

    /// <summary>Text outline scaled uniformly to fit the area (minus a 1 px margin) and centred in it.</summary>
    private GraphicsPath FitText(string text, RectangleF area)
    {
        var path = new GraphicsPath();
        path.AddString(text, _digitFont, (int)FontStyle.Bold, 100f, Point.Empty, StringFormat.GenericTypographic);
        // Flatten first: bounds of the curved outline can be off for some fonts.
        path.Flatten();
        var bounds = path.GetBounds();
        if (bounds.Width > 0 && bounds.Height > 0)
        {
            var scale = Math.Min((area.Width - 1f) / bounds.Width, (area.Height - 0.5f) / bounds.Height);
            using var matrix = new Matrix();
            matrix.Translate(area.X + area.Width / 2f, area.Y + area.Height / 2f);
            matrix.Scale(scale, scale);
            matrix.Translate(-(bounds.X + bounds.Width / 2f), -(bounds.Y + bounds.Height / 2f));
            path.Transform(matrix);
        }
        return path;
    }

    /// <summary>The tray's small icon size, or a fixed size for previews.</summary>
    public int IconSize { get; init; } = Math.Max(16, SystemInformation.SmallIconSize.Width);

    /// <summary>
    /// Whether the taskbar is light. The connected headphones are drawn white on a dark taskbar
    /// and near-black on a light one. Changing it redraws the icons.
    /// </summary>
    public bool LightTaskbar
    {
        get => _lightTaskbar;
        set
        {
            if (_lightTaskbar == value)
            {
                return;
            }
            _lightTaskbar = value;
            foreach (var icon in _icons.Values)
            {
                icon.Dispose();
            }
            _icons.Clear();
        }
    }

    private bool _lightTaskbar;

    private Color ColorFor(HeadsetState state) => state switch
    {
        HeadsetState.On => _lightTaskbar ? Color.FromArgb(0x1C, 0x1C, 0x1C) : Color.White,
        HeadsetState.Off => Color.FromArgb(0x9A, 0x9A, 0x9A),
        HeadsetState.ReceiverMissing => Color.FromArgb(0xF0, 0xA0, 0x30),
        _ => Color.FromArgb(0x60, 0x60, 0x60),
    };

    private static Color BatteryColor(BatteryStatus battery) => battery switch
    {
        { Charging: true } => BatteryCharging,
        { Percent: <= 15 } => BatteryCritical,
        { Percent: <= 30 } => BatteryLow,
        _ => BatteryGood,
    };

    /// <summary>
    /// Headphones alone, or headphones with a rounded battery bar underneath. Drawn at the tray's
    /// real icon size so nothing is blurred by scaling.
    /// </summary>
    private Icon Draw(Color color, BatteryStatus? battery)
    {
        var size = IconSize;
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            if (battery is null)
            {
                var glyph = size * 0.8f;
                DrawHeadphones(g, color, GlyphBox(size, (size - glyph) / 2f, glyph));
            }
            else
            {
                // Bottom to top: 1 px margin, the bar, a gap, then the glyph in what's left.
                var barHeight = MathF.Max(3, MathF.Round(size * 0.2f));
                var gap = MathF.Max(2, MathF.Round(size * 0.12f));
                var bar = new RectangleF(1, size - 1 - barHeight, size - 2, barHeight);
                var glyph = bar.Top - gap;
                DrawHeadphones(g, color, GlyphBox(size, 0, glyph));
                DrawBattery(g, battery, bar);
            }
        }

        return ToIcon(bitmap);
    }

    /// <summary>A box of the given height with the glyph's 26:25 aspect ratio, centred horizontally.</summary>
    private static RectangleF GlyphBox(int iconSize, float top, float height)
    {
        var width = MathF.Min(iconSize, height * 26f / 25f);
        return new RectangleF((iconSize - width) / 2f, top, width, height);
    }

    private static Icon ToIcon(Bitmap bitmap)
    {
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

    /// <summary>Headphones glyph scaled into a box (designed on a 26×25 grid).</summary>
    private static void DrawHeadphones(Graphics g, Color color, RectangleF box)
    {
        float w = box.Width, h = box.Height;
        var penWidth = Math.Max(1.5f, w * 4f / 26f);
        using var pen = new Pen(color, penWidth);
        var top = box.Y + penWidth / 2f;
        g.DrawArc(pen, box.X + w * 2f / 26f, top, w * 22f / 26f, (h - penWidth / 2f) * 0.96f, 180, 180);
        using var brush = new SolidBrush(color);
        var cupHeight = h * 14f / 25f;
        g.FillRectangle(brush, box.X, box.Bottom - cupHeight, w * 8f / 26f, cupHeight);
        g.FillRectangle(brush, box.X + w * 18f / 26f, box.Bottom - cupHeight, w * 8f / 26f, cupHeight);
    }

    /// <summary>Pill-shaped battery bar: a dark track with the charge filled in from the left.</summary>
    private static void DrawBattery(Graphics g, BatteryStatus battery, RectangleF bar)
    {
        // One anti-aliased fill of the pill, with a brush that switches hard from the battery colour
        // to the track colour at the charge level. (Clipping a fill to the pill isn't anti-aliased,
        // which squares off the ends on a bar only a few pixels tall.)
        using var pill = RoundedRect(bar, bar.Height / 2f);
        var level = Math.Clamp(battery.Percent, 0, 100) / 100f;
        var fillColor = BatteryColor(battery);
        var trackColor = Color.FromArgb(0x50, 0x50, 0x50);

        using var brush = new LinearGradientBrush(
            new RectangleF(bar.X - 1, bar.Y, bar.Width + 2, bar.Height), fillColor, trackColor, LinearGradientMode.Horizontal);
        // Map the level from the bar onto the (slightly wider) brush rectangle.
        var step = Math.Clamp((1 + level * bar.Width) / (bar.Width + 2), 0.001f, 0.999f);
        brush.InterpolationColors = new ColorBlend
        {
            Colors = [fillColor, fillColor, trackColor, trackColor],
            Positions = [0f, step, step, 1f],
        };
        g.FillPath(brush, pill);
    }

    private static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        var diameter = Math.Min(radius * 2f, Math.Min(rect.Width, rect.Height));
        var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
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
        _digitFont.Dispose();
    }
}
