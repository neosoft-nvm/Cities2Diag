namespace Cs2Monitor.Detection;

public enum DetectorPhase { Idle, Warmup, Normal, Warning, Stall, Recovery }

/// <summary>A stall the detector found; finished once the post-stall window has elapsed.</summary>
public sealed class DetectedStall
{
    public double StartTMs;
    public double EndTMs;                 // last abnormal sample; NaN while ongoing
    public DateTime StartUtc;
    public bool Truncated;
    public string? EndReason;
    public bool ConfirmedByUser;
    public Dictionary<string, double> Baselines = new();
    public Dictionary<string, int> RuleCounts = new();
    public double DurationMs => (double.IsNaN(EndTMs) ? StartTMs : EndTMs) - StartTMs;
}

/// <summary>
/// Stall detector. Fed finalised ("settled") samples in time order from the sampler thread.
///
///   Normal ──abnormal──▶ Warning ──≥MinStallMs and ≥MinAbnormalFraction──▶ Stall
///     ▲                     │ quiet for EndHoldMs                            │ quiet for EndHoldMs
///     │                     ▼                                                ▼
///     └────────────────── Normal ◀──PostEventSeconds elapsed── Recovery ──abnormal again──▶ Stall
///
/// A sample is abnormal when any enabled rule fires against its baseline (median of recent normal samples).
/// </summary>
public sealed class StallDetector
{
    private readonly DetectionSettings _cfg;
    private readonly List<(DetectionRule Rule, Metric Metric)> _rules = new();
    private readonly Queue<Sample> _baseline = new();
    private readonly List<double> _scratch = new();

    private double _warmupStart = double.NaN;
    private double _warnStart, _lastAbnormal;
    private int _warnTotal, _warnAbnormal;
    private double _recoveryAbnormalStart = double.NaN;
    private readonly List<Sample> _warnSamples = new();

    public DetectorPhase Phase { get; private set; } = DetectorPhase.Idle;
    public DetectedStall? Current { get; private set; }
    public IReadOnlyList<string> RuleProblems { get; }

    public StallDetector(DetectionSettings cfg)
    {
        _cfg = cfg;
        var problems = new List<string>();
        foreach (var rule in cfg.Rules)
        {
            if (!rule.Enabled) continue;
            var metric = Metrics.Find(rule.Metric);
            if (metric == null) { problems.Add($"rule '{rule.Name}': unknown metric '{rule.Metric}'"); continue; }
            if (rule.Absolute == null && rule.Delta == null && rule.Factor == null) { problems.Add($"rule '{rule.Name}': no condition set"); continue; }
            _rules.Add((rule, metric));
        }
        RuleProblems = problems;
    }

    public void Reset()
    {
        Phase = DetectorPhase.Idle;
        Current = null;
        _baseline.Clear();
        _warnSamples.Clear();
        _warmupStart = double.NaN;
    }

    /// <summary>Processes one sample. Returns a stall when its post window has completed (ready to be saved).</summary>
    public DetectedStall? Process(Sample s)
    {
        if (!_cfg.Enabled || !s.GameRunning)
        {
            if (Phase != DetectorPhase.Idle) Reset();
            s.Phase = "idle";
            return null;
        }

        if (Phase == DetectorPhase.Idle)
        {
            Phase = DetectorPhase.Warmup;
            _warmupStart = s.TMs;
        }

        var (fired, context) = Evaluate(s);
        s.Abnormal = fired.Count > 0;
        s.RulesFired = fired.Count > 0 ? string.Join("; ", fired) : null;
        s.ContextFired = context.Count > 0 ? string.Join("; ", context) : null;

        DetectedStall? finished = null;
        switch (Phase)
        {
            case DetectorPhase.Warmup:
                AddBaseline(s);
                if (s.TMs - _warmupStart >= _cfg.WarmupSeconds * 1000.0) Phase = DetectorPhase.Normal;
                break;

            case DetectorPhase.Normal:
                if (s.Abnormal)
                {
                    Phase = DetectorPhase.Warning;
                    _warnStart = _lastAbnormal = s.TMs;
                    _warnTotal = _warnAbnormal = 1;
                    _warnSamples.Clear();
                    _warnSamples.Add(s);
                }
                else AddBaseline(s);
                break;

            case DetectorPhase.Warning:
                _warnTotal++;
                _warnSamples.Add(s);
                if (s.Abnormal) { _warnAbnormal++; _lastAbnormal = s.TMs; }
                if (s.TMs - _lastAbnormal >= _cfg.EndHoldMs)
                {
                    // Too short: it was noise. Let those samples inform the baseline after all.
                    Phase = DetectorPhase.Normal;
                    foreach (var w in _warnSamples) AddBaseline(w);
                    _warnSamples.Clear();
                }
                else if (s.TMs - _warnStart >= _cfg.MinStallMs && (double)_warnAbnormal / _warnTotal >= _cfg.MinAbnormalFraction)
                {
                    Phase = DetectorPhase.Stall;
                    Current = new DetectedStall
                    {
                        StartTMs = _warnStart,
                        StartUtc = _warnSamples[0].Utc,
                        EndTMs = double.NaN,
                        Baselines = CurrentBaselines(),
                    };
                    foreach (var w in _warnSamples) Count(w);
                    _warnSamples.Clear();
                }
                break;

            case DetectorPhase.Stall:
                Count(s);
                if (s.Abnormal) _lastAbnormal = s.TMs;
                if (s.TMs - _lastAbnormal >= _cfg.EndHoldMs)
                {
                    Phase = DetectorPhase.Recovery;
                    Current!.EndTMs = _lastAbnormal;
                    _recoveryAbnormalStart = double.NaN;
                }
                else if (s.TMs - Current!.StartTMs >= _cfg.MaxStallSeconds * 1000.0)
                {
                    Phase = DetectorPhase.Recovery;
                    Current.EndTMs = s.TMs;
                    Current.Truncated = true;
                    Current.EndReason = $"still abnormal after {_cfg.MaxStallSeconds} s (cut off)";
                    _recoveryAbnormalStart = double.NaN;
                }
                break;

            case DetectorPhase.Recovery:
                if (!Current!.Truncated && s.Abnormal)
                {
                    // Sustained relapse shortly after the stall: same event.
                    if (double.IsNaN(_recoveryAbnormalStart)) _recoveryAbnormalStart = s.TMs;
                    if (s.TMs - _recoveryAbnormalStart >= _cfg.EndHoldMs)
                    {
                        Phase = DetectorPhase.Stall;
                        Current.EndTMs = double.NaN;
                        _lastAbnormal = s.TMs;
                        break;
                    }
                }
                else _recoveryAbnormalStart = double.NaN;

                if (s.TMs - Current.EndTMs >= _cfg.PostEventSeconds * 1000.0)
                {
                    finished = Current;
                    Current = null;
                    Phase = DetectorPhase.Normal;
                    if (finished.Truncated)
                    {
                        // The "abnormal" state persisted for minutes: treat it as the new normal and re-learn.
                        _baseline.Clear();
                        Phase = DetectorPhase.Warmup;
                        _warmupStart = s.TMs;
                    }
                }
                break;
        }

        s.Phase = Phase.ToString().ToLowerInvariant();
        return finished;
    }

    /// <summary>Ends an ongoing stall early (game exited). Returns it so it can still be saved.</summary>
    public DetectedStall? Abort(string reason, double lastTMs)
    {
        var c = Current;
        if (c != null)
        {
            if (double.IsNaN(c.EndTMs)) { c.EndTMs = lastTMs; c.Truncated = true; }
            c.EndReason ??= reason;
        }
        Reset();
        return c;
    }

    private (List<string> Triggers, List<string> Context) Evaluate(Sample s)
    {
        var fired = new List<string>();
        var context = new List<string>();
        if (Phase == DetectorPhase.Warmup) return (fired, context);
        foreach (var (rule, metric) in _rules)
        {
            var v = metric.Get(s);
            if (v == null || !double.IsFinite(v.Value)) continue;
            var b = Median(metric);
            if (Fires(rule, v.Value, b)) (rule.IsContext ? context : fired).Add(rule.Name);
        }
        return (fired, context);
    }

    internal static bool Fires(DetectionRule r, double v, double? baseline)
    {
        bool above = !r.Direction.Equals("Below", StringComparison.OrdinalIgnoreCase);
        if ((r.Delta.HasValue || r.Factor.HasValue) && baseline == null) return false;
        if (r.Absolute.HasValue && (above ? v < r.Absolute : v > r.Absolute)) return false;
        if (r.Delta.HasValue && (above ? v < baseline + r.Delta : v > baseline - r.Delta)) return false;
        if (r.Factor.HasValue && (above ? v < baseline * r.Factor : v > baseline * r.Factor)) return false;
        return true;
    }

    private void Count(Sample s)
    {
        if (Current == null) return;
        if (s.RulesFired != null)
            foreach (var name in s.RulesFired.Split("; "))
                Current.RuleCounts[name] = Current.RuleCounts.GetValueOrDefault(name) + 1;
        if (s.ContextFired != null)
            foreach (var name in s.ContextFired.Split("; "))
                Current.RuleCounts[name + " (context)"] = Current.RuleCounts.GetValueOrDefault(name + " (context)") + 1;
    }

    public void ConfirmByUser()
    {
        if (Current != null) Current.ConfirmedByUser = true;
    }

    private void AddBaseline(Sample s)
    {
        _baseline.Enqueue(s);
        double cutoff = s.TMs - _cfg.BaselineSeconds * 1000.0;
        while (_baseline.Count > 0 && _baseline.Peek().TMs < cutoff) _baseline.Dequeue();
    }

    /// <summary>Current baseline (median of recent normal samples) for any metric; null during warm-up.</summary>
    public double? Baseline(Metric metric) => Phase == DetectorPhase.Idle ? null : Median(metric);

    private double? Median(Metric metric)
    {
        _scratch.Clear();
        foreach (var b in _baseline)
        {
            var v = metric.Get(b);
            if (v.HasValue && double.IsFinite(v.Value)) _scratch.Add(v.Value);
        }
        if (_scratch.Count < 5) return null;
        _scratch.Sort();
        int mid = _scratch.Count / 2;
        return _scratch.Count % 2 == 1 ? _scratch[mid] : (_scratch[mid - 1] + _scratch[mid]) / 2;
    }

    private Dictionary<string, double> CurrentBaselines()
    {
        var d = new Dictionary<string, double>();
        foreach (var (_, metric) in _rules)
            if (Median(metric) is double m) d[metric.Key] = m;
        return d;
    }
}
