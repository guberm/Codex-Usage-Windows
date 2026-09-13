using System.IO;
using System.Windows;

namespace CodexUsage;
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var smoke = args.Length == 2 && args[0] == "--smoke-test" ? args[1] : null;
        var demo = smoke != null || args.Contains("--demo");
        using var mutex = new Mutex(true, "Local\\CodexUsage.Windows", out var first);
        using var activate = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\CodexUsage.Windows.Activate");
        if (!first && !demo) { activate.Set(); return 0; }
        var directory = demo ? Path.Combine(Path.GetTempPath(), "codex-usage-demo-" + Guid.NewGuid()) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexUsage");
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var window = new MainWindow(new Storage(directory), demo, smoke);
        var registration = ThreadPool.RegisterWaitForSingleObject(activate, (_, _) => app.Dispatcher.BeginInvoke(() => window.ShowPanel()), null, -1, false);
        try { return app.Run(window); }
        finally { registration.Unregister(null); if (demo && Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
