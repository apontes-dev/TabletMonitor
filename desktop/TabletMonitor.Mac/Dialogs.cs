using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace TabletMonitor.Mac;

/// <summary>Caixas de diálogo simples (o Avalonia não tem MessageBox pronto).</summary>
internal static class Dialogs
{
    private static Window Make(string title, Control body, params (string Text, bool Result)[] buttons)
    {
        var w = new Window
        {
            Title = title, SizeToContent = SizeToContent.WidthAndHeight, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, MaxWidth = 560,
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        foreach (var (text, result) in buttons)
        {
            var b = new Button { Content = text, IsDefault = result, IsCancel = !result };
            b.Click += (_, _) => w.Close(result);
            row.Children.Add(b);
        }
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        panel.Children.Add(body);
        panel.Children.Add(row);
        w.Content = panel;
        return w;
    }

    public static Task Info(Window owner, string title, string text) =>
        Make(title, new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, ("OK", true)).ShowDialog<bool>(owner);

    public static async Task<Adb.ProvisionOptions?> Provision(Window owner)
    {
        var debloat = new CheckBox { Content = "Desativar apps desnecessários (YouTube, Chrome, Gmail, apps do fabricante…) — reversível", IsChecked = true };
        var store = new CheckBox { Content = "Desativar também a Play Store", IsChecked = false };
        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = "Isto vai instalar o TabletMonitor no tablet e torná-lo \"dono do dispositivo\": vira a tela inicial, " +
                   "sem barra de status, sem tela de bloqueio e sem como sair para outros apps.\n\n" +
                   "IMPORTANTE: o Android só permite isso se NÃO houver nenhuma conta (Google, Samsung…) no tablet.",
        });
        body.Children.Add(debloat);
        body.Children.Add(store);
        bool ok = await Make("Preparar tablet — modo dedicado", body, ("Cancelar", false), ("Preparar", true)).ShowDialog<bool>(owner);
        return ok ? new Adb.ProvisionOptions(debloat.IsChecked == true, store.IsChecked == true) : null;
    }
}
