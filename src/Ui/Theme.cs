using Microsoft.Win32;

namespace LogiSwitch.Ui;

/// <summary>Colours for the tray menu, following the Windows taskbar theme.</summary>
internal sealed record Theme(
    bool IsLight,
    Color Background,
    Color Border,
    Color Text,
    Color DisabledText,
    Color Hover,
    Color Separator)
{
    public static readonly Theme Dark = new(
        IsLight: false,
        Background: Color.FromArgb(0x2C, 0x2C, 0x2C),
        Border: Color.FromArgb(0x45, 0x45, 0x45),
        Text: Color.FromArgb(0xF2, 0xF2, 0xF2),
        DisabledText: Color.FromArgb(0x8A, 0x8A, 0x8A),
        Hover: Color.FromArgb(0x3D, 0x3D, 0x3D),
        Separator: Color.FromArgb(0x48, 0x48, 0x48));

    public static readonly Theme Light = new(
        IsLight: true,
        Background: Color.FromArgb(0xF9, 0xF9, 0xF9),
        Border: Color.FromArgb(0xD5, 0xD5, 0xD5),
        Text: Color.FromArgb(0x1A, 0x1A, 0x1A),
        DisabledText: Color.FromArgb(0x8A, 0x8A, 0x8A),
        Hover: Color.FromArgb(0xEA, 0xEA, 0xEA),
        Separator: Color.FromArgb(0xE0, 0xE0, 0xE0));

    /// <summary>
    /// The theme the taskbar and tray use: "Choose your default Windows mode" in Settings.
    /// Falls back to dark when the setting can't be read.
    /// </summary>
    public static Theme Current
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("SystemUsesLightTheme") is int value && value != 0 ? Light : Dark;
            }
            catch
            {
                return Dark;
            }
        }
    }
}
