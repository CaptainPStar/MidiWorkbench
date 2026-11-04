using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;

namespace MidiWorkbench.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => AppBuilder.Configure<WorkbenchApp>()
        .UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);
}
public sealed class WorkbenchApp : Avalonia.Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Styles.Add(new Dock.Avalonia.Themes.Fluent.DockFluentTheme());
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
    }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
