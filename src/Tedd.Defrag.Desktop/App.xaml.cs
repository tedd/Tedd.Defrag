using Microsoft.Maui.Storage;

namespace Tedd.Defrag.Desktop;

public partial class App : Application
{
    private const string ThemePreferenceKey = "appearance.theme";

    private static readonly IReadOnlyDictionary<string, string> LightPalette = new Dictionary<string, string>
    {
        ["Canvas"] = "#F6F9FA", ["Surface"] = "#FFFFFF", ["Line"] = "#D6E1E5", ["Accent"] = "#087F7B",
        ["Ink"] = "#13232C", ["Muted"] = "#5E727D", ["ButtonSurface"] = "#E7EEF1", ["Field"] = "#EDF3F5",
        ["Placeholder"] = "#728691", ["SwitchThumb"] = "#FFFFFF", ["Track"] = "#D8E2E6", ["Sidebar"] = "#F1F6F7",
        ["SidebarLine"] = "#D8E2E5", ["SidebarText"] = "#48616D", ["NavSelected"] = "#D5EEEC", ["NoticeSurface"] = "#E3F4F3",
        ["NoticeLine"] = "#BEDDDA", ["NoticeText"] = "#3D6669", ["BadgeSurface"] = "#E9F0F5", ["BadgeLine"] = "#D1DCE4",
        ["BadgeText"] = "#465E73", ["MapCanvas"] = "#0C141F", ["SubtleText"] = "#6C8290", ["DangerSurface"] = "#F5E8EC",
        ["DangerText"] = "#9B485B", ["ActionSurface"] = "#E8F5F4", ["ActionLine"] = "#B7D7D5", ["ActionInner"] = "#F6FAFA",
        ["ActionButton"] = "#D5E8E8", ["PrimaryText"] = "#062C2F", ["HeaderSurface"] = "#EEF3F5", ["Overlay"] = "#9913232C",
        ["ModalSurface"] = "#FFFFFF", ["ModalLine"] = "#96C3C0", ["OptionSurface"] = "#F6F9FA", ["OptionStroke"] = "#D0DEE2",
        ["OptionIconSurface"] = "#DDF5F2", ["OptionIconStroke"] = "#ABD0CD", ["Chevron"] = "#647B88"
    };

    private static readonly IReadOnlyDictionary<string, string> DarkPalette = new Dictionary<string, string>
    {
        ["Canvas"] = "#0A1018", ["Surface"] = "#101A26", ["Line"] = "#233140", ["Accent"] = "#54D5CB",
        ["Ink"] = "#E5EEF4", ["Muted"] = "#91A2B4", ["ButtonSurface"] = "#1B2B3A", ["Field"] = "#152231",
        ["Placeholder"] = "#667D92", ["SwitchThumb"] = "#E5EEF4", ["Track"] = "#29394C", ["Sidebar"] = "#0D1621",
        ["SidebarLine"] = "#1D2937", ["SidebarText"] = "#A0B2C5", ["NavSelected"] = "#193A40", ["NoticeSurface"] = "#11262C",
        ["NoticeLine"] = "#27424A", ["NoticeText"] = "#94AEB9", ["BadgeSurface"] = "#182536", ["BadgeLine"] = "#2B3C50",
        ["BadgeText"] = "#B7C9DF", ["MapCanvas"] = "#0C141F", ["SubtleText"] = "#7B93A9", ["DangerSurface"] = "#352735",
        ["DangerText"] = "#E6AFB8", ["ActionSurface"] = "#12242C", ["ActionLine"] = "#34565B", ["ActionInner"] = "#10202A",
        ["ActionButton"] = "#23424B", ["PrimaryText"] = "#082329", ["HeaderSurface"] = "#152130", ["Overlay"] = "#E6080D14",
        ["ModalSurface"] = "#0F1925", ["ModalLine"] = "#3B666B", ["OptionSurface"] = "#121F2C", ["OptionStroke"] = "#263A4B",
        ["OptionIconSurface"] = "#173239", ["OptionIconStroke"] = "#2C5559", ["Chevron"] = "#668095"
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
