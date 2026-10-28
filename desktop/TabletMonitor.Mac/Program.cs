using Avalonia;

namespace TabletMonitor.Mac;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        App.StartHidden = args.Contains("--minimized");
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .StartWithClassicDesktopLifetime(args);
    }
}
