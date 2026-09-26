using System;
using System.Collections.Generic;
using Game.Pathfind;

namespace PerformanceDetective
{
    /// <summary>
    /// Which game systems generate pathfinding work. Reads the game's own per-requester statistics
    /// (PathfindResultSystem.queryStats: query count, successes, and the share of the road graph each search explored)
    /// every few seconds and turns the cumulative counters into rates over the last minute.
    /// Read-only; runs on the main thread between simulation updates.
    /// </summary>
    public sealed class PathfindSources
    {
        public sealed class Source
        {
            public string System;        // game system type name
            public string Name;          // player-facing
            public string Tip;           // community-reported fix, if any
            public double PerMinute;
            public double WorkShare;     // share of graph traversal (search effort) in the window, 0..1
            public double SuccessRate = double.NaN;
            public long SessionQueries;
            public double SessionWork;
        }

        private struct Totals { public long Count, Success; public double Work; }

        private const double WindowSeconds = 60;
        private readonly Queue<(double T, Dictionary<string, Totals> Totals)> m_Snapshots = new Queue<(double, Dictionary<string, Totals>)>();
        private Dictionary<string, Totals> m_SessionStart;
        private readonly Dictionary<string, string> m_TypeByName = new Dictionary<string, string>();
        public List<Source> Top { get; private set; } = new List<Source>();
        public double TotalPerMinute { get; private set; }
        public bool Available { get; private set; } = true;

        public void Reset()
        {
            m_Snapshots.Clear();
            m_SessionStart = null;
            Top = new List<Source>();
            TotalPerMinute = 0;
        }

        public void Update(PathfindResultSystem results, double t)
        {
            if (!Available || results == null) return;
            Dictionary<string, Totals> now;
            try
            {
                now = new Dictionary<string, Totals>();
                foreach (var kv in results.queryStats)
                {
                    // Group by player-facing name (e.g. all police systems together).
                    string type = kv.Key.m_System?.GetType().Name ?? "Unknown";
                    string name = Describe(type).Name;
                    if (!m_TypeByName.ContainsKey(name)) m_TypeByName[name] = type;
                    now.TryGetValue(name, out var tot);
                    tot.Count += kv.Value.m_QueryCount;
                    tot.Success += kv.Value.m_SuccessCount;
                    tot.Work += kv.Value.m_GraphTraversal;
                    now[name] = tot;
                }
            }
            catch (Exception e)
            {
                Available = false; // game changed: keep the rest of the mod working
                Mod.Log.Warn("[SPC] Compatibility: pathfinding statistics unavailable: " + e.Message);
                return;
            }

            // Counters are cumulative and reset when a city is loaded.
            if (m_Snapshots.Count > 0 && Sum(now) < Sum(LastSnapshot())) Reset();
            if (m_SessionStart == null) m_SessionStart = now;

            m_Snapshots.Enqueue((t, now));
            while (m_Snapshots.Count > 2 && m_Snapshots.Peek().T < t - WindowSeconds) m_Snapshots.Dequeue();
            var (t0, then) = m_Snapshots.Peek();
            double minutes = (t - t0) / 60.0;
            if (minutes <= 0) return;

            var list = new List<Source>();
            double totalWork = 0, totalQueries = 0;
            foreach (var kv in now)
            {
                then.TryGetValue(kv.Key, out var old);
                m_SessionStart.TryGetValue(kv.Key, out var first);
                long dq = kv.Value.Count - old.Count;
                double dw = kv.Value.Work - old.Work;
                long ds = kv.Value.Success - old.Success;
                totalWork += Math.Max(0, dw);
                totalQueries += Math.Max(0, dq);
                string type = m_TypeByName.TryGetValue(kv.Key, out var t1) ? t1 : kv.Key;
                list.Add(new Source
                {
                    System = type, Name = kv.Key, Tip = Describe(type).Tip,
                    PerMinute = dq / minutes,
                    WorkShare = dw,
                    SuccessRate = dq > 0 ? (double)ds / dq : double.NaN,
                    SessionQueries = kv.Value.Count - first.Count,
                    SessionWork = kv.Value.Work - first.Work,
                });
            }
            foreach (var s in list) s.WorkShare = totalWork > 0 ? Math.Max(0, s.WorkShare) / totalWork : 0;
            list.Sort((a, b) => b.WorkShare.CompareTo(a.WorkShare) != 0 ? b.WorkShare.CompareTo(a.WorkShare) : b.PerMinute.CompareTo(a.PerMinute));
            TotalPerMinute = totalQueries / minutes;
            Top = list;
        }

        private Dictionary<string, Totals> LastSnapshot()
        {
            Dictionary<string, Totals> last = null;
            foreach (var s in m_Snapshots) last = s.Totals;
            return last;
        }

        private static long Sum(Dictionary<string, Totals> d)
        {
            long n = 0;
            if (d != null) foreach (var v in d.Values) n += v.Count;
            return n;
        }

        /// <summary>Player-facing names; tips are what other players reported, not verified fixes.</summary>
        public static (string Name, string Tip) Describe(string system)
        {
            switch (system)
            {
                case "TaxiDispatchSystem":
                    return ("Taxi dispatch", "Players report large gains from fewer taxi depots (one 360k city: 74% → 94% simulation speed, pending requests 250 → 20 after removing 3). Test on a copy of your save.");
                case "TaxiAISystem": return ("Taxis driving", "See taxi dispatch: fewer taxi depots means fewer taxis searching for fares.");
                case "HouseholdFindPropertySystem":
                    return ("Households looking for a home", "Includes homeless households, which keep searching until housed. Fewer homeless (affordable housing, shelters) reduces these searches.");
                case "PersonalCarAISystem":
                    return ("Private cars re-routing / parking", "Congestion and full car parks make cars look for new routes and parking again and again. Players report gains from better traffic flow and parking with its own access roads.");
                case "ResidentAISystem": return ("Residents on the move (re-routing)", "Often rises with congestion and missing or overloaded transit.");
                case "TripNeededSystem": return ("New trips (citizens)", null);
                case "LeisureSystem": return ("Leisure trips", null);
                case "FindJobSystem": return ("Job seekers", null);
                case "FindSchoolSystem": return ("School seekers", null);
                case "TouristFindTargetSystem": return ("Tourists choosing destinations", null);
                case "ResourceBuyerSystem": return ("Buying goods (companies and households)", "Supply chains: shops and industry fetching goods. Players report gains from truck routes separated from commuter traffic.");
                case "GoodsDeliveryDispatchSystem": return ("Goods delivery dispatch", null);
                case "DeliveryTruckAISystem": return ("Delivery trucks", null);
                case "StorageTransferSystem": return ("Warehouse / storage transfers", null);
                case "ResourceExporterSystem": return ("Exports", null);
                case "RandomTrafficDispatchSystem": return ("Outside / random traffic", null);
                case "TransportVehicleDispatchSystem": return ("Public transport dispatch", null);
                case "TransportCarAISystem": return ("Buses and other transit vehicles", null);
                case "TransportTrainAISystem": return ("Trains, trams and subways", null);
                case "TransportAircraftAISystem": return ("Passenger aircraft", null);
                case "TransportWatercraftAISystem": return ("Ships and ferries", null);
                case "VehicleLaunchSystem": return ("Vehicles launching", null);
                case "WorkCarAISystem": case "WorkWatercraftAISystem": return ("Work vehicles", null);
                case "AreaLotSimulationSystem": return ("Area / lot vehicles", null);
                case "GarbageCollectorDispatchSystem": case "GarbageTransferDispatchSystem": case "GarbageTruckAISystem": return ("Garbage service", null);
                case "PoliceEmergencyDispatchSystem": case "PolicePatrolDispatchSystem": case "PoliceCarAISystem": case "PoliceAircraftAISystem": case "PrisonerTransportDispatchSystem": return ("Police", null);
                case "FireRescueDispatchSystem": case "FireEngineAISystem": case "FireAircraftAISystem": return ("Fire service", null);
                case "HealthcareDispatchSystem": case "AmbulanceAISystem": case "HearseAISystem": case "MedicalAircraftAISystem": return ("Health and deathcare", null);
                case "PostVanDispatchSystem": case "PostVanAISystem": case "MailTransferDispatchSystem": return ("Mail", null);
                case "MaintenanceVehicleDispatchSystem": case "MaintenanceVehicleAISystem": return ("Road and park maintenance", null);
                case "EvacuationDispatchSystem": return ("Evacuation", null);
                default: return (system.EndsWith("System") ? system.Substring(0, system.Length - 6) : system, null);
            }
        }
    }
}
