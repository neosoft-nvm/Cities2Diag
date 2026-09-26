using Cs2Monitor.UI;

namespace Cs2Monitor;

internal static class Program
{
    /// <summary>
    /// Normal use: no arguments.
    /// Testing without the game:  Cs2Monitor.exe --process msedge --logdir C:\temp\logs --headless 10
    ///   --settings FILE    use this settings file (read-only) instead of Documents\CS2StallInvestigator\settings.json
    ///   --process NAME     monitor another process instead of Cities2 (settings file is not changed)
    ///   --logdir DIR       write sessions somewhere else
    ///   --headless SEC     no window; sample for SEC seconds, then exit
    ///   --capture-at SEC   (headless) simulate pressing Capture Event SEC seconds after start
    ///   --gpu-engine 0|1, --threads 0|1, --frames 0|1   switch collectors off/on
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        int settingsArg = Array.IndexOf(args, "--settings");
        var settings = MonitorSettings.Load(settingsArg >= 0 && settingsArg + 1 < args.Length ? args[settingsArg + 1] : null);
        int headlessSeconds = 0;
        var captures = new List<double>();
        for (int i = 0; i + 1 < args.Length; i += 2)
        {
            switch (args[i])
            {
                case "--process": settings.ProcessName = args[i + 1]; break;
                case "--logdir": settings.LogDirectory = args[i + 1]; break;
                case "--headless": headlessSeconds = int.Parse(args[i + 1]); break;
                case "--capture-at": captures.Add(double.Parse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture)); break;
                case "--gpu-engine": settings.EnableGpuEngineCounters = args[i + 1] != "0"; break;
                case "--threads": settings.EnableThreadSampling = args[i + 1] != "0"; break;
                case "--frames": settings.FrameTimingEnabled = args[i + 1] != "0"; break;
            }
        }

        using var sampler = new Sampler(settings);
        sampler.Start();

        if (headlessSeconds > 0)
        {
            var start = DateTime.UtcNow;
            foreach (var at in captures.OrderBy(c => c))
            {
                var wait = start.AddSeconds(at) - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) Thread.Sleep(wait);
                sampler.Capture("test");
            }
            var rest = start.AddSeconds(headlessSeconds) - DateTime.UtcNow;
            if (rest > TimeSpan.Zero) Thread.Sleep(rest);
            return sampler.Error == null ? 0 : 1;
        }

        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm(settings, sampler));
        return 0;
    }
}
