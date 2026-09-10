using Microsoft.Maui.Storage;

namespace Tedd.Defrag.Desktop;

public partial class App : Application
{
    private const string ThemePreferenceKey = "appearance.theme";

    private static readonly IReadOnlyDictionary<string, string> LightPalette = new Dictionary<string, string>
    {
        ["Canvas"] = "#EEF2F3", ["Surface"] = "#FAFBFB", ["Line"] = "#CAD5DA", ["Accent"] = "#0B8045",
        ["Ink"] = "#172630", ["Muted"] = "#60727D", ["ButtonSurface"] = "#DFE7EA", ["Field"] = "#E5EBED",
        ["Placeholder"] = "#71838E", ["SwitchThumb"] = "#FFFFFF", ["Track"] = "#CDD8DC", ["Sidebar"] = "#E1E8EA",
        ["SidebarLine"] = "#C6D2D7", ["SidebarText"] = "#485D69", ["NavSelected"] = "#CDE5D7", ["NoticeSurface"] = "#DFEDE5",
        ["NoticeLine"] = "#BAD5C6", ["NoticeText"] = "#426456", ["BadgeSurface"] = "#DDE7EA", ["BadgeLine"] = "#C4D1D7",
        ["BadgeText"] = "#435D69", ["MapCanvas"] = "#172531", ["SubtleText"] = "#71848F", ["DangerSurface"] = "#F2E5E8",
        ["DangerText"] = "#98495B", ["ActionSurface"] = "#DFEDE5", ["ActionLine"] = "#B1CEBE", ["ActionInner"] = "#F1F6F3",
        ["ActionButton"] = "#C7DED1", ["PrimaryText"] = "#FFFFFF", ["HeaderSurface"] = "#E2E9EC", ["Overlay"] = "#9917232D",
        ["ModalSurface"] = "#FAFBFB", ["ModalLine"] = "#7EAD94", ["OptionSurface"] = "#F1F4F5", ["OptionStroke"] = "#CCD7DC",
        ["OptionIconSurface"] = "#D7EADF", ["OptionIconStroke"] = "#A5CBB5", ["Chevron"] = "#657A86",
        ["Allocated"] = "#1D8BEB", ["Fragmented"] = "#F3BF3E", ["Metadata"] = "#37C3DB", ["Excluded"] = "#526772",
        ["FreeSpace"] = "#455B6B", ["Activity"] = "#EDF2F4", ["Highlight"] = "#FFD45A"
    };

    private static readonly IReadOnlyDictionary<string, string> DarkPalette = new Dictionary<string, string>
    {
        ["Canvas"] = "#172431", ["Surface"] = "#1C2B38", ["Line"] = "#2B3D4B", ["Accent"] = "#18B563",
        ["Ink"] = "#EEF3F5", ["Muted"] = "#9AA8B2", ["ButtonSurface"] = "#253847", ["Field"] = "#223340",
        ["Placeholder"] = "#7F929E", ["SwitchThumb"] = "#F5F8F6", ["Track"] = "#344755", ["Sidebar"] = "#243645",
        ["SidebarLine"] = "#314553", ["SidebarText"] = "#A8B5BE", ["NavSelected"] = "#2B4651", ["NoticeSurface"] = "#1D3C35",
        ["NoticeLine"] = "#2D5A49", ["NoticeText"] = "#A1B6AD", ["BadgeSurface"] = "#273947", ["BadgeLine"] = "#354958",
        ["BadgeText"] = "#B9C6CE", ["MapCanvas"] = "#172531", ["SubtleText"] = "#7F919D", ["DangerSurface"] = "#453039",
        ["DangerText"] = "#F0BAC2", ["ActionSurface"] = "#1B303B", ["ActionLine"] = "#31505A", ["ActionInner"] = "#192934",
        ["ActionButton"] = "#29404D", ["PrimaryText"] = "#07150D", ["HeaderSurface"] = "#243643", ["Overlay"] = "#D917232D",
        ["ModalSurface"] = "#1B2A37", ["ModalLine"] = "#367353", ["OptionSurface"] = "#20313E", ["OptionStroke"] = "#304552",
        ["OptionIconSurface"] = "#1E4437", ["OptionIconStroke"] = "#2C684D", ["Chevron"] = "#7F929D",
        ["Allocated"] = "#1D8BEB", ["Fragmented"] = "#F3BF3E", ["Metadata"] = "#37C3DB", ["Excluded"] = "#8295A3",
        ["FreeSpace"] = "#455B6B", ["Activity"] = "#EDF2F4", ["Highlight"] = "#FFD45A"
    };

    public string ThemePreference { get; private set; } = "System";

    public App()
    {
        InitializeComponent();
        SetThemePreference(Preferences.Default.Get(ThemePreferenceKey, "System"), persist: false);
        RequestedThemeChanged += (_, args) => ApplyPalette(args.RequestedTheme);
    }

    public void SetThemePreference(string preference, bool persist = true)
    {
        ThemePreference = preference is "Light" or "Dark" ? preference : "System";
        UserAppTheme = ThemePreference switch
        {
            "Light" => AppTheme.Light,
            "Dark" => AppTheme.Dark,
            _ => AppTheme.Unspecified
        };

        if (persist)
            Preferences.Default.Set(ThemePreferenceKey, ThemePreference);

        ApplyPalette(ThemePreference switch
        {
            "Light" => AppTheme.Light,
            "Dark" => AppTheme.Dark,
            _ => RequestedTheme
        });
    }

    private void ApplyPalette(AppTheme theme)
    {
        var palette = theme == AppTheme.Dark ? DarkPalette : LightPalette;
        foreach (var (key, value) in palette)
            Resources[key] = Color.FromArgb(value);
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var page = new MainPage();
        var window = new Window(page)
        { Title = "Tedd Defrag · Advanced Defrag, Open Source, Free", Width = 1440, Height = 960, MinimumWidth = 1160, MinimumHeight = 760 };
        window.Destroying += (_, _) => page.Shutdown();
        return window;
    }
}
