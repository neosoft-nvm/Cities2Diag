using System;
using System.Collections.Generic;

namespace PerformanceDetective
{
    /// <summary>
    /// Detects "slow motion" directly from the simulation: the number of simulation ticks per real second,
    /// compared with what is normal for this city at the selected speed (rolling median of recent normal samples).
    ///
    ///   normal ──ratio &lt; threshold──▶ slow ──≥ MinStall s, ≥ 70 % slow──▶ stall ──quiet 1 s──▶ catch-up watch ──▶ normal
    ///
    /// After a stall the game usually runs faster than normal for a while (the "warp speed"); that is measured as
    /// catch-up. Paused, loading, or a speed change resets the baseline for that speed.
    /// </summary>
    internal sealed class StallDetector
    {
        private const double BaselineSeconds = 60;
        private const int MinBaselineSamples = 25;
        private const double EndHoldSeconds = 1.0;
        private const double MinSlowFraction = 0.7;
        private const double CatchUpRatio = 1.25;
        private const double CatchUpWatchSeconds = 20;

        private readonly Dictionary<float, Queue<(double T, double Ticks)>> _baselines = new Dictionary<float, Queue<(double, double)>>();
        private readonly List<double> _scratch = new List<double>();

        private string _state = "normal";
        private double _slowStart, _lastSlow;
        private int _slowTotal, _slowCount;
        private StallEvent _current;
        private double _catchUpStart = double.NaN;

        public double ThresholdRatio = 0.5;
        public double MinStallSeconds = 2.0;

        /// <summary>A stall whose catch-up phase is over (ready to be saved), or null.</summary>
        public StallEvent Process(Sample s)
        {
            if (s.Loading || s.SelectedSpeed <= 0)
            {
                s.State = s.Loading ? "loading" : "paused";
                var aborted = _current;
                if (aborted != null && double.IsNaN(aborted.EndT)) aborted.EndT = s.T;
                _current = null;
                _state = "normal";
                return aborted; // a stall interrupted by pausing/loading is still worth keeping
            }

            double? baseline = Baseline(s.SelectedSpeed);
            s.Ratio = baseline is double b && b > 0 ? s.TicksPerSec / b : double.NaN;
            if (double.IsNaN(s.Ratio))
            {
                s.State = "learning";
                AddBaseline(s);
                return null;
            }

            bool slow = s.Ratio < ThresholdRatio;
            StallEvent finished = null;
            switch (_state)
            {
                case "normal":
                    if (slow)
                    {
                        _state = "slow";
                        _slowStart = _lastSlow = s.T;
                        _slowTotal = _slowCount = 1;
                    }
                    else AddBaseline(s);
                    break;

                case "slow":
                    _slowTotal++;
                    if (slow) { _slowCount++; _lastSlow = s.T; }
                    if (s.T - _lastSlow >= EndHoldSeconds) _state = "normal";
                    else if (s.T - _slowStart >= MinStallSeconds && (double)_slowCount / _slowTotal >= MinSlowFraction)
                    {
                        _state = "stall";
                        _current = new StallEvent
                        {
                            StartT = _slowStart,
                            StartUtc = s.Utc.AddSeconds(_slowStart - s.T),
                            EndT = double.NaN,
                            BaselineTicksPerSec = baseline.Value,
                            SelectedSpeed = s.SelectedSpeed,
                            MinRatio = s.Ratio,
                        };
                    }
                    break;

                case "stall":
                    if (slow) _lastSlow = s.T;
                    _current.MinRatio = Math.Min(_current.MinRatio, s.Ratio);
                    if (s.T - _lastSlow >= EndHoldSeconds)
                    {
                        _current.EndT = _lastSlow;
                        _state = "watch";
                        _catchUpStart = double.NaN;
                    }
                    break;

                case "watch":
                    if (s.Ratio >= CatchUpRatio)
                    {
                        if (double.IsNaN(_catchUpStart)) _catchUpStart = s.T;
                        _current.CatchUpSeconds = s.T - _catchUpStart + s.IntervalMs / 1000.0;
                        _current.CatchUpPeakRatio = double.IsNaN(_current.CatchUpPeakRatio) ? s.Ratio : Math.Max(_current.CatchUpPeakRatio, s.Ratio);
                    }
                    else if (slow && s.T - _current.EndT < 3)
                    {
                        _state = "stall"; // relapse right after the stall: same event
                        _lastSlow = s.T;
                        break;
                    }
                    if (s.T - _current.EndT >= CatchUpWatchSeconds)
                    {
                        finished = _current;
                        _current = null;
                        _state = "normal";
                    }
                    break;
            }

            s.State = _state == "watch" ? (s.Ratio >= CatchUpRatio ? "catchup" : "recovered") : _state;
            return finished;
        }

        public bool InStall => _current != null;

        public void ConfirmByUser()
        {
            if (_current != null) _current.ConfirmedByUser = true;
        }

        /// <summary>Normal simulation ticks/s for this speed, or null while still learning.</summary>
        public double? Baseline(float speed)
        {
            if (!_baselines.TryGetValue(speed, out var q) || q.Count < MinBaselineSamples) return null;
            _scratch.Clear();
            foreach (var x in q) _scratch.Add(x.Ticks);
            _scratch.Sort();
            return _scratch[_scratch.Count / 2];
        }

        private void AddBaseline(Sample s)
        {
            if (!_baselines.TryGetValue(s.SelectedSpeed, out var q))
                _baselines[s.SelectedSpeed] = q = new Queue<(double, double)>();
            q.Enqueue((s.T, s.TicksPerSec));
            while (q.Count > 0 && q.Peek().T < s.T - BaselineSeconds) q.Dequeue();
        }
    }
}
