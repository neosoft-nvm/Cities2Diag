namespace PerformanceDetective.Controller
{
    public enum Priority { Low = 0, MediumLow = 1, Medium = 2 }

    /// <summary>
    /// One game system the controller may run less often.
    /// Only "Class A" systems are listed: each run processes one rotating slice of entities (UpdateFrame), so a skipped
    /// run delays that slice by one cycle and loses nothing. See docs/CONTROLLER_DESIGN.md.
    /// Verified against the game as of 2026-09-26; a system that no longer exists is simply not controlled.
    /// </summary>
    public sealed class ControlTarget
    {
        public string Key;          // stable id used in settings/UI
        public string Name;         // player-facing name
        public string Effect;       // what the player may notice
        public string TypeName;     // game system type
        public Priority Priority;   // lower priority is reduced first
    }

    public static class ControlTargets
    {
        public static readonly ControlTarget[] All =
        {
            new ControlTarget
            {
                Key = "pets", Name = "Pets", TypeName = "Game.Simulation.HouseholdPetBehaviorSystem", Priority = Priority.Low,
                Effect = "Pets decide what to do less often.",
            },
            new ControlTarget
            {
                Key = "tourists", Name = "Tourist households", TypeName = "Game.Simulation.TouristHouseholdBehaviorSystem", Priority = Priority.Low,
                Effect = "Tourists re-evaluate their plans less often.",
            },
            new ControlTarget
            {
                Key = "events", Name = "Event attendance", TypeName = "Game.Simulation.FindEventAttendantsSystem", Priority = Priority.Low,
                Effect = "Citizens are recruited for city events less often.",
            },
            new ControlTarget
            {
                // Slice-based (UpdateFrame); requests stay queued as entities, so slower dispatch = longer waits, no losses.
                Key = "taxi", Name = "Taxi dispatch", TypeName = "Game.Simulation.TaxiDispatchSystem", Priority = Priority.Low,
                Effect = "Taxis are matched to waiting passengers less often: longer waits, far fewer route searches. (Players report big gains from fewer taxi depots — this does it without demolishing anything.)",
            },
            new ControlTarget
            {
                // Processes up to 1,280 homeless + 128 other households per run; unprocessed households simply wait.
                Key = "homeSearch", Name = "Households looking for a home", TypeName = "Game.Simulation.HouseholdFindPropertySystem", Priority = Priority.MediumLow,
                Effect = "Households (including homeless ones) search for a new home less often. Moving in and out happens more slowly.",
            },
            new ControlTarget
            {
                Key = "happiness", Name = "Citizen wellbeing", TypeName = "Game.Simulation.CitizenHappinessSystem", Priority = Priority.MediumLow,
                Effect = "Happiness and health values update more slowly.",
            },
            new ControlTarget
            {
                Key = "workers", Name = "Workers", TypeName = "Game.Simulation.WorkerSystem", Priority = Priority.Medium,
                Effect = "Work-related citizen state updates less often.",
            },
            new ControlTarget
            {
                Key = "citizens", Name = "Citizen daily decisions", TypeName = "Game.Simulation.CitizenBehaviorSystem", Priority = Priority.Medium,
                Effect = "Citizens decide where to go next less often (also fewer new trips to pathfind).",
            },
        };
    }

    /// <summary>
    /// Data-driven mapping from overall simulation quality (0–100) to how much each priority tier is reduced.
    /// reduction = max × clamp((100 − quality − start) / (full − start), 0, 1)
    /// </summary>
    public static class QualityCurve
    {
        private struct Tier { public float Start, Full, Max; }

        private static readonly Tier[] Tiers =
        {
            new Tier { Start = 0, Full = 40, Max = 0.75f },   // Low: sacrificed first, up to 75 % fewer runs
            new Tier { Start = 10, Full = 60, Max = 0.5f },   // MediumLow
            new Tier { Start = 25, Full = 80, Max = 0.5f },   // Medium: only at lower quality, at most half
        };

        public static float Reduction(Priority p, float quality)
        {
            var t = Tiers[(int)p];
            float x = (100f - quality - t.Start) / (t.Full - t.Start);
            if (x < 0) x = 0;
            if (x > 1) x = 1;
            return t.Max * x;
        }
    }

    public enum Profile { MaximumAccuracy = 0, Balanced = 1, Performance = 2, ExtremePerformance = 3, Custom = 4 }

    public static class Profiles
    {
        /// <summary>Overall quality for each preset (Custom uses per-category settings instead).</summary>
        public static float Quality(Profile p)
        {
            switch (p)
            {
                case Profile.MaximumAccuracy: return 100;
                case Profile.Balanced: return 85;
                case Profile.Performance: return 60;
                case Profile.ExtremePerformance: return 30;
                default: return 100;
            }
        }

        public static string Label(Profile p)
        {
            switch (p)
            {
                case Profile.MaximumAccuracy: return "Maximum Accuracy";
                case Profile.Balanced: return "Balanced";
                case Profile.Performance: return "Performance";
                case Profile.ExtremePerformance: return "Extreme Performance";
                default: return "Custom";
            }
        }
    }
}
