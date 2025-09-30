// NobleSociety Addon: MeetingTracker
// Records lightweight “has met” flags for NPC lords based on co-presence
// (same settlement or same army) on a daily sweep.

using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Library;
using TaleWorlds.SaveSystem;
using NSLog = NobleSociety.Logging.FileLogger;

namespace NobleSociety.Addons
{
    public class MeetingTracker : CampaignBehaviorBase
    {
        private const float RememberDays = 120f;  // forget old meetings after ~4 months
        private static readonly bool DEBUG = false;

        private static void Log(string msg)
        {
            if (!DEBUG) return;
            NSLog.Log("[MEETING] " + msg);
        }

        private static MeetingTracker _instance;

        [SaveableField(1)] private Dictionary<string, CampaignTime> _lastSeen; // key = PairKey(a,b) unordered

        public MeetingTracker()
        {
            _lastSeen = new Dictionary<string, CampaignTime>();
            _instance = this;
        }

        public override void RegisterEvents()
        {
            CampaignEvents.DailyTickEvent.AddNonSerializedListener(this, OnDailyTick);
        }

        public override void SyncData(IDataStore dataStore)
        {
            // Pack Dictionary<string, CampaignTime> as parallel lists
            var keys = new List<string>();
            var valsDays = new List<float>();

            if (dataStore.IsSaving)
            {
                foreach (var kv in _lastSeen)
                {
                    keys.Add(kv.Key);
                    valsDays.Add((float)kv.Value.ToDays); // <-- cast double -> float
                }
            }

            dataStore.SyncData("meetingTracker_lastSeen_keys", ref keys);
            dataStore.SyncData("meetingTracker_lastSeen_valsDays", ref valsDays);

            if (dataStore.IsLoading)
            {
                _lastSeen = new Dictionary<string, CampaignTime>(keys.Count);
                for (int i = 0; i < Math.Min(keys.Count, valsDays.Count); i++)
                    _lastSeen[keys[i]] = CampaignTime.Days(valsDays[i]);
            }

            _instance = this;
        }

        private void OnDailyTick()
        {
            try
            {
                var lords = Hero.AllAliveHeroes.Where(h => h.IsLord && !h.IsChild).ToList();

                // group by settlement presence
                var bySettlement = lords.Where(h => h.CurrentSettlement != null)
                                        .GroupBy(h => h.CurrentSettlement);
                foreach (var grp in bySettlement)
                    RecordCoPresence(grp.ToList(), $"settlement:{grp.Key?.Name}");

                // group by army membership
                var byArmy = lords.Where(h => h.PartyBelongedTo?.Army != null)
                                  .GroupBy(h => h.PartyBelongedTo.Army);
                foreach (var grp in byArmy)
                    RecordCoPresence(grp.ToList(), "army");

                // periodic cleanup
                ForgetOld(olderThanDays: RememberDays);
            }
            catch (Exception ex)
            {
                NSLog.Log("[MEETING] ERROR OnDailyTick: " + ex);
            }
        }

        private void RecordCoPresence(List<Hero> heroes, string context)
        {
            if (heroes == null || heroes.Count < 2) return;

            for (int i = 0; i < heroes.Count; i++)
                for (int j = i + 1; j < heroes.Count; j++)
                {
                    var a = heroes[i];
                    var b = heroes[j];
                    string key = PairKey(a, b);
                    _lastSeen[key] = CampaignTime.Now;
                    Log($"met {a?.Name} & {b?.Name} via {context}");
                }
        }

        private void ForgetOld(float olderThanDays)
        {
            var cutoff = CampaignTime.Now - CampaignTime.Days(olderThanDays);
            var toRemove = _lastSeen.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList();
            foreach (var k in toRemove) _lastSeen.Remove(k);
            if (toRemove.Count > 0) Log($"forgot {toRemove.Count} old meeting pairs");
        }

        private static string PairKey(Hero a, Hero b)
        {
            if (a == null || b == null) return "null|null";
            var x = a.StringId; var y = b.StringId;
            return (string.CompareOrdinal(x, y) < 0) ? (x + "|" + y) : (y + "|" + x);
        }

        // ===== Public static API =====
        public static bool HasMetWithinDays(Hero a, Hero b, float withinDays)
        {
            if (_instance == null) return false;
            if (a == null || b == null) return false;

            var key = PairKey(a, b);
            if (!_instance._lastSeen.TryGetValue(key, out var t)) return false;
            return t.ElapsedDaysUntilNow <= withinDays;
        }
    }
}
