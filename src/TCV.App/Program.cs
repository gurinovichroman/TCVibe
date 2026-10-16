using TCV.App.Settings;
using Velopack;

namespace TCV.App;

/// <summary>Entry point: Velopack's hooks, then the state folder, then WPF.</summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // TRAP-71: while it updates, Velopack starts this exe with --veloapp-* arguments; Run()
        // answers them and ends the process. Nothing may run before it — not logging, not DI.
        // Auto-apply is off (Velopack's default is on): a downloaded update would otherwise install
        // whenever any process of this copy starts — a workspace window, a second launch — and the
        // updater kills the window that is still running from current\ (TRAP-72). An update goes in
        // only through ServiceCommands.InstallUpdateAsync.
        VelopackApp.Build().SetAutoApplyOnStartup(false).Run();

        // TRAP-70: in the portable layout the binaries are replaced by every update; state goes to
        // data\, or to the folder given with --data.
        PortableLayout.Initialize(StartupOptions.Parse(args).DataDirectory);

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
