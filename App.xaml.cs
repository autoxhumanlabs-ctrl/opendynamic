using System.Linq;
using System.Threading;
using System.Windows;

namespace Island;

public partial class App : Application
{
    static Mutex? single;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Contains("--selftest")) { Logic.SelfTest(); Shutdown(0); return; }
        single = new Mutex(true, "Island.SingleInstance", out bool first);
        // Opening the app again (desktop icon) while it runs: tell the running one to show itself.
        var show = new EventWaitHandle(false, EventResetMode.AutoReset, "Island.Show");
        if (!first) { show.Set(); Shutdown(); return; }
        // Measured on this layered window: software ~1-2.5% of a core collapsed (GPU 8.5%, readback every frame);
        // open card ~15% software vs ~12% GPU, only while hovered. Software wins overall.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        // Smoothness: a busy browser/game shouldn't make the island stutter, and no blocking full GC mid-animation.
        System.Diagnostics.Process.GetCurrentProcess().PriorityClass = System.Diagnostics.ProcessPriorityClass.AboveNormal;
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
        base.OnStartup(e);
        var win = new MainWindow();
        win.Show();
        new Thread(() => { while (show.WaitOne()) win.Dispatcher.BeginInvoke(win.ShowFromLauncher); }) { IsBackground = true }.Start();
    }
}
