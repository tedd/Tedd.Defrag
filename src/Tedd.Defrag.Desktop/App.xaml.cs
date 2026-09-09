namespace Tedd.Defrag.Desktop;
public partial class App : Application
{
    public App() { InitializeComponent(); UserAppTheme = AppTheme.Dark; }
    protected override Window CreateWindow(IActivationState? activationState) => new(new MainPage())
    { Title = "Tedd Defrag · Advanced Defrag, Open Source, Free", Width = 1440, Height = 960, MinimumWidth = 1160, MinimumHeight = 760 };
}
