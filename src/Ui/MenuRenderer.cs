using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace LogiSwitch.Ui;

/// <summary>
/// Windows 11-style tray menu: flat background, rounded hover highlight, roomy items and
/// rounded window corners, in the colours of <see cref="Theme"/>.
/// </summary>
internal sealed class MenuRenderer : ToolStripProfessionalRenderer
{
    private const int HoverInset = 4;
    private readonly Theme _theme;

    public MenuRenderer(Theme theme)
        : base(new FlatColorTable(theme))
    {
        _theme = theme;
        RoundedEdges = false;
    }

    /// <summary>Applies the theme to a menu and all its submenus. Call before showing it.</summary>
    public static void Apply(ContextMenuStrip menu, Theme theme)
    {
        var renderer = new MenuRenderer(theme);
        Style(menu, renderer);
    }

    private static void Style(ToolStripDropDown dropDown, MenuRenderer renderer)
    {
        dropDown.Renderer = renderer;
        dropDown.BackColor = renderer._theme.Background;
        dropDown.ForeColor = renderer._theme.Text;
        dropDown.Padding = new Padding(2, 4, 2, 4);
        // The classic drop shadow is rectangular and clashes with rounded corners.
        dropDown.DropShadowEnabled = false;
        dropDown.Opened -= OnOpened;
        dropDown.Opened += OnOpened;

        foreach (ToolStripItem item in dropDown.Items)
        {
            if (item is ToolStripSeparator)
            {
                continue;
            }
            item.Padding = new Padding(4, 5, 4, 5);
            if (item is ToolStripMenuItem { HasDropDownItems: true } parent)
            {
                Style(parent.DropDown, renderer);
            }
        }
    }

    private static void OnOpened(object? sender, EventArgs e)
    {
        if (sender is ToolStripDropDown { Renderer: MenuRenderer renderer } dropDown)
        {
            WindowChrome.Round(dropDown.Handle, renderer._theme.Border);
        }
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(_theme.Background);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        // DWM draws the (rounded) border; see WindowChrome.
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(_theme.Background);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled)
        {
            return;
        }

        var bounds = new Rectangle(HoverInset, 1, e.Item.Width - HoverInset * 2, e.Item.Height - 2);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundedRect(bounds, 4);
        using var brush = new SolidBrush(_theme.Hover);
        e.Graphics.FillPath(brush, path);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? _theme.Text : _theme.DisabledText;
        // The menu item lays text out at its pre-padding height, so it sits near the top.
        // Stretch the text box to the full item height and centre it there.
        if (e.Item is ToolStripMenuItem)
        {
            var r = e.TextRectangle;
            e.TextRectangle = new Rectangle(r.X, 0, r.Width, e.Item.Height);
            e.TextFormat = (e.TextFormat & ~(TextFormatFlags.Top | TextFormatFlags.Bottom))
                | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;
        }
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = e.Item?.Enabled == false ? _theme.DisabledText : _theme.Text;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var y = e.Item.Height / 2;
        using var pen = new Pen(_theme.Separator);
        e.Graphics.DrawLine(pen, HoverInset + 4, y, e.Item.Width - HoverInset - 4, y);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        // The stock check mark is a black bitmap, invisible on dark menus; draw our own.
        var r = e.ImageRectangle;
        var cx = r.X + r.Width / 2f;
        var cy = e.Item.Height / 2f;                 // centre on the item, like the text
        var s = Math.Min(r.Width, r.Height) / 2.6f;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(e.Item.Enabled ? _theme.Text : _theme.DisabledText, 1.6f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        e.Graphics.DrawLines(pen, new[]
        {
            new PointF(cx - s, cy),
            new PointF(cx - s * 0.35f, cy + s * 0.65f),
            new PointF(cx + s, cy - s * 0.7f),
        });
    }

    private static GraphicsPath RoundedRect(Rectangle rect, int radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Flat colours for the parts of the professional renderer we don't override.</summary>
    private sealed class FlatColorTable(Theme theme) : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => theme.Background;
        public override Color ImageMarginGradientBegin => theme.Background;
        public override Color ImageMarginGradientMiddle => theme.Background;
        public override Color ImageMarginGradientEnd => theme.Background;
        public override Color MenuBorder => theme.Border;
        public override Color MenuItemBorder => Color.Transparent;
        public override Color MenuItemSelected => theme.Hover;
        public override Color SeparatorDark => theme.Separator;
        public override Color SeparatorLight => theme.Separator;
        public override Color CheckBackground => Color.Transparent;
        public override Color CheckSelectedBackground => Color.Transparent;
        public override Color CheckPressedBackground => Color.Transparent;
    }
}

/// <summary>Windows 11 rounded corners and border colour for popup windows (no-op on Windows 10).</summary>
internal static class WindowChrome
{
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwcpRoundSmall = 3;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Round(IntPtr hwnd, Color border)
    {
        var corner = DwmwcpRoundSmall;
        DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref corner, sizeof(int));
        var colorRef = border.R | (border.G << 8) | (border.B << 16);   // COLORREF is 0x00BBGGRR
        DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref colorRef, sizeof(int));
    }
}
