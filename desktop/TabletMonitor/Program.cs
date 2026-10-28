namespace TabletMonitor;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(true, "TabletMonitor-SingleInstance-7f3c", out bool first);
        if (!first)
        {
            MessageBox.Show("O TabletMonitor já está aberto (veja o ícone na bandeja, perto do relógio).", "TabletMonitor");
            return;
        }

        ApplicationConfiguration.Initialize();
        var settings = AppSettings.Load();
        bool minimized = args.Contains("--minimized") || settings.StartMinimized;
        Application.Run(new MainForm(settings, minimized));
    }
}
