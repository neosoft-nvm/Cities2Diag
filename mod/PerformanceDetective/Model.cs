using System;

namespace PerformanceDetective
{
    /// <summary>One measurement (default: every 200 ms of real time). Null / NaN = not measured.</summary>
    public sealed class Sample
    {
        public DateTime Utc;
        public double T;                 // seconds since the session started (real time)
        public double IntervalMs;        // actual time covered by this sample

        // Simulation
        public bool Loading;
        public float SelectedSpeed;      // speed the player chose (0 = paused)
        public float SmoothSpeed;        // speed reported by the game's SimulationSystem
        public double TicksPerSec;       // simulation frames advanced per real second (measured)
        public double Ratio = double.NaN;// TicksPerSec / normal rate at this speed (1.0 = normal)

        // Rendering
        public int Frames;
        public double FrameMsAvg;
        public double FrameMsMax;

        // Game process
        public double GameCores = double.NaN;      // CPU time / wall time, 1.0 = one logical processor fully used
        public double PrivateMb = double.NaN;
        public double WorkingSetMb = double.NaN;
        public double PageFaultsPerSec = double.NaN;

        // System
        public double SystemCpuPct = double.NaN;
        public double RamAvailMb = double.NaN;
        public double RamTotalMb = double.NaN;
        public double CommitUsedMb = double.NaN;
        public double CommitLimitMb = double.NaN;

        // City
        public int Population = -1;

        // Detector / markers
        public string State = "";        // normal, slow, stall, catchup, paused, loading, learning
        public string Marker;            // capture / save written / …
    }

    /// <summary>A finished stall (or a manual capture), ready to be written as an event.</summary>
    public sealed class StallEvent
    {
        public int Id;
        public string Kind = "stall";    // stall | capture
        public bool ConfirmedByUser;
        public double StartT;
        public double EndT;
        public DateTime StartUtc;
        public double MinRatio = double.NaN;
        public double AvgRatio = double.NaN;
        public double CatchUpSeconds;    // time after the stall with the simulation running faster than normal
        public double CatchUpPeakRatio = double.NaN;
        public double BaselineTicksPerSec = double.NaN;
        public float SelectedSpeed;
        public string SaveWrittenNearby; // save file written within ±30 s
        public double DurationSec => EndT - StartT;
    }
}
