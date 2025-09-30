// NobleSociety Addon: CourtshipTelemetry
// Tracks time from CourtshipStarted to Marriage for NPC pairs, so you can tune pacing.

using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.SaveSystem;
using NSLog = NobleSociety.Logging.FileLogger;

namespace NobleSociety.Addons
{
    public class CourtshipTelemetry : CampaignBehaviorBase
    {
        private static readonly bool DEBUG = true;

        private static CourtshipTelemetry _instance;

        [SaveableField(1)] private Dictionary<string, CampaignTime> _startTimes;    // PairKey(a,b)
        [SaveableField(2)] private List<float> _durationsDays;

        public CourtshipTelemetry()
        {
            _startTimes = new Dictionary<string, CampaignTime>();
            _durationsDays = new List<float>();
            _instance = this;
        }

        public override void RegisterEvents()
        {
            // NpcRomanceCampaignBehavior calls our public methods
        }

        public override void SyncData(IDataStore dataStore)
        {
            // Pack _startTimes: (keys, valuesDays)
            var stKeys = new List<string>();
            var stValsDays = new List<float>();
            if (dataStore.IsSaving)
            {
                foreach (var kv in _startTimes)
                {
                    stKeys.Add(kv.Key);
                    stValsDays.Add((float)kv.Value.ToDays); // <-- cast double -> float
                }
            }
            dataStore.SyncData("telemetry_startTimes_keys", ref stKeys);
            dataStore.SyncData("telemetry_startTimes_valsDays", ref stValsDays);
            if (dataStore.IsLoading)
            {
                _startTimes = new Dictionary<string, CampaignTime>(stKeys.Count);
                for (int i = 0; i < Math.Min(stKeys.Count, stValsDays.Count); i++)
                    _startTimes[stKeys[i]] = CampaignTime.Days(stValsDays[i]);
            }

            // _durationsDays is a simple List<float> — safe to sync directly
            dataStore.SyncData("telemetry_durations", ref _durationsDays);

            _instance = this;
        }

        private static void Log(string msg)
        {
            if (!DEBUG) return;
            NSLog.Log("[COURTSHIP-TELEMETRY] " + msg);
        }

        private static string PairKey(Hero a, Hero b)
        {
            var x = a.StringId; var y = b.StringId;
            return (string.CompareOrdinal(x, y) < 0) ? (x + "|" + y) : (y + "|" + x);
        }

        // ===== Public API =====
        public static void MarkCourtshipStart(Hero a, Hero b)
        {
            if (_instance == null || a == null || b == null) return;
            var key = PairKey(a, b);
            if (!_instance._startTimes.ContainsKey(key))
            {
                _instance._startTimes[key] = CampaignTime.Now;
                Log($"start {a?.Name} ↔ {b?.Name} @ {CampaignTime.Now}");
            }
        }

        public static void MarkMarriage(Hero a, Hero b)
        {
            if (_instance == null || a == null || b == null) return;
            var key = PairKey(a, b);
            if (_instance._startTimes.TryGetValue(key, out var started))
            {
                float days = (float)(CampaignTime.Now - started).ToDays;
                _instance._durationsDays.Add(days);
                _instance._startTimes.Remove(key);

                double avg = _instance._durationsDays.Count > 0 ? _instance._durationsDays.Average() : 0.0;
                Log($"married {a?.Name} ↔ {b?.Name} after {days:0.0} days. Samples={_instance._durationsDays.Count}, avg={avg:0.0}");
            }
            else
            {
                Log($"marriage without start recorded for {a?.Name} ↔ {b?.Name}");
            }
        }

        public static void ForgetHero(Hero h)
        {
            if (_instance == null || h == null) return;
            var sid = h.StringId;
            var toRemove = _instance._startTimes.Keys.Where(k => k.Contains(sid)).ToList();
            foreach (var k in toRemove) _instance._startTimes.Remove(k);
            if (toRemove.Count > 0) Log($"cleanup removed {toRemove.Count} pending courtships involving {h?.Name}");
        }

        // Optional: quick summary for console/logs
        public static string Summary()
        {
            if (_instance == null || _instance._durationsDays.Count == 0) return "No samples yet.";

            var sorted = _instance._durationsDays.ToList();
            sorted.Sort();

            float avg = (float)sorted.Average();
            float p50 = sorted[sorted.Count / 2];
            float p90 = sorted[(int)(sorted.Count * 0.9f)];

            return $"Courtship samples={sorted.Count}, avg={avg:0.0}d, p50={p50:0.0}d, p90={p90:0.0}d";
        }
    }
}
