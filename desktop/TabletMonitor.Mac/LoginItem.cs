namespace TabletMonitor.Mac;

/// <summary>"Abrir ao iniciar a sessão" via LaunchAgent do usuário (não precisa de permissão especial).</summary>
internal static class LoginItem
{
    private static string PlistPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", "com.apontes.tabletmonitor.plist");

    public static void Apply(bool enabled, Action<string> log)
    {
        try
        {
            if (!enabled)
            {
                if (File.Exists(PlistPath)) File.Delete(PlistPath);
                return;
            }
            string exe = Environment.ProcessPath ?? "";
            Directory.CreateDirectory(Path.GetDirectoryName(PlistPath)!);
            File.WriteAllText(PlistPath, $"""
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key><string>com.apontes.tabletmonitor</string>
  <key>ProgramArguments</key>
  <array>
    <string>{System.Security.SecurityElement.Escape(exe)}</string>
    <string>--minimized</string>
  </array>
  <key>RunAtLoad</key><true/>
  <key>ProcessType</key><string>Interactive</string>
</dict>
</plist>
""");
        }
        catch (Exception ex) { log("Abrir ao iniciar: " + ex.Message); }
    }
}
