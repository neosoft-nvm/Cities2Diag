using Cs2Monitor.UI;

namespace Cs2Monitor;

internal static class Program
{
    /// <summary>
    /// Normal use: no arguments.
    /// Testing without the game:  Cs2Monitor.exe --process msedge --logdir C:\temp\logs --headless 10
    ///   --process NAME   monitor another process instead of Cities2 (settings file is not changed)
    ///   --logdir DIR     write sessions somewhere else
    ///   --headless SEC   no window; sample for SEC seconds, then exit
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        var settings = MonitorSettings.Load();
        int headlessSeconds = 0;
        for (int i = 0; i + 1 < args.Length; i += 2)
        {
            switch (args[i])
            {
                case "--process": settings.ProcessName = args[i + 1]; break;
                case "--logdir": settings.LogDirectory = args[i + 1]; break;
                case "--headless": headlessSeconds = int.Parse(args[i + 1]); break;
                case "--gpu-engine": settings.EnableGpuEngineCounters = args[i + 1] != "0"; break;
                case "--threads": settings.EnableThreadSampling = args[i + 1] != "0"; break;
            }
        }

        using var sampler = new Sampler(settings);
        sampler.Start();

        if (headlessSeconds > 0)
        {
            Thread.Sleep(TimeSpan.FromSeconds(headlessSeconds));
            return sampler.Error == null ? 0 : 1;
        }

        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm(settings, sampler));
        return 0;
    }
}
