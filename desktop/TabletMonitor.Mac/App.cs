using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace TabletMonitor.Mac;

public sealed class App : Application
{
    private TrayIcon? _tray;
    private MainWindow? _main;
    public static new App? Current => Application.Current as App;
    public static bool StartHidden { get; set; }

    public static Stream IconStream() =>
        typeof(App).Assembly.GetManifestResourceStream("icon.png") ?? throw new InvalidOperationException("ícone ausente");

    public override void Initialize()
    {
        Name = "TabletMonitor";
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Default; // segue o modo claro/escuro do macOS
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var settings = AppSettings.Load();
            _main = new MainWindow(settings);
            if (!StartHidden) _main.Show();
            else _main.Opened += (_, _) => { };

            var menu = new NativeMenu();
            var open = new NativeMenuItem("Abrir TabletMonitor");
            open.Click += (_, _) => ShowMain();
            var quit = new NativeMenuItem("Sair");
            quit.Click += (_, _) =>
            {
                _main.AllowExit = true;
                _main.Shutdown();
                desktop.Shutdown();
            };
            menu.Items.Add(open);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(quit);
            _tray = new TrayIcon { Icon = new WindowIcon(IconStream()), ToolTipText = "TabletMonitor", Menu = menu, IsVisible = true };
            _tray.Clicked += (_, _) => ShowMain();
            TrayIcon.SetIcons(this, new TrayIcons { _tray });

            // Clicar no ícone do Dock com a janela escondida: mostra de novo.
            if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime act)
                act.Activated += (_, e) => { if (e.Kind == ActivationKind.Reopen) ShowMain(); };

            // Iniciado escondido (login): a janela precisa existir para o servidor iniciar.
            if (StartHidden) { _main.Show(); _main.Hide(); }
        }
        base.OnFrameworkInitializationCompleted();
    }

    public void ShowMain()
    {
        if (_main == null) return;
        _main.Show();
        _main.Activate();
    }

    public void SetTrayTooltip(string text)
    {
        if (_tray != null) _tray.ToolTipText = text;
    }
}
