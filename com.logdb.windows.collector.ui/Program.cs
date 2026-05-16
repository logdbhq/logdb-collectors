using Avalonia;
using Velopack;

namespace com.logdb.windows.collector.ui;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build()
            .OnFirstRun(version =>
            {
                Console.WriteLine($"Collector UI first run: {version}");
            })
            .OnRestarted(version =>
            {
                Console.WriteLine($"Collector UI restarted after update: {version}");
            })
            .Run();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
