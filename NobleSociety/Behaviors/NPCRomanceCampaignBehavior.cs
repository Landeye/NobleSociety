// NobleSociety Addon: NPC Romance & Rivalry Behavior (low-noise logging)
// Purpose: Let AI nobles progress romance like the player (courtship stages) and compete as rivals,
//          while logging only high-signal events to the file log.

using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.SaveSystem;
using NSLog = NobleSociety.Logging.FileLogger;

namespace NobleSociety.Addons
{
    public class NpcRomanceCampaignBehavior : CampaignBehaviorBase
    {
        // ======= CONFIG =======
        private const float RomanceCooldownDays = 3f;      // per-pair action cooldown
        private const int MaxConcurrentSuitors = 3;        // raised cap
        private const float BaseAdvanceChance = 0.25f;

        // Rivalry: single symmetric relation delta (Bannerlord relations are shared)
        private const int RivalryPairPenalty = -3;
        private const float RivalryTickDays = 10f;         // your 10-day month

        // Run main logic only every N days per clan
        private const float ClanProcessIntervalDays = 3f;

        // ======= DEBUG / LOGGING =======
        private static readonly bool DEBUG = true;          // master on/off for *all* file logging
        private static readonly bool LOG_VERBOSE = false;   // when false: whitelist only high-signal lines

        private static float Clamp(float v, float min, float max) => v < min ? min : (v > max ? max : v);

        private static bool IsImportant(string msg)
        {
            // Whitelist: summarize & milestone events only
            if (msg.StartsWith("MARRIAGE:")) return true;
            if (msg.StartsWith("CourtshipStarted:")) return true;
            if (msg.Contains(" → CoupleAgreedOnMarriage")) return true;
            if (msg.StartsWith("Rivalry tick summary")) return true;
            if (msg.StartsWith("Rivalry fallout summary")) return true;
            if (msg.StartsWith("Cleanup on death")) return true;
            if (msg.StartsWith("Daily cleanup:")) return true;
            if (msg.StartsWith("Clan ")) return true;                 // per-clan end-of-tick summary
            if (msg.StartsWith("War declared:")) return true;
            // Everything else is considered verbose/noisy
            return false;
        }

        private static void Log(string msg)
        {
            if (!DEBUG) return;
            if (!LOG_VERBOSE && !IsImportant(msg)) return; // drop noisy lines unless explicitly verbose
            NSLog.Log("[NPC-ROMANCE] " + msg);
        }

        private static string H(Hero h) => h == null ? "null" : $"{h.Name}({h.StringId})";
        private static string C(Clan c) => c == null ? "null" : $"{c.Name}";

        // ======= SAVEABLE STATE =======
        [SaveableField(1)] private Dictionary<string, CampaignTime> _lastApproach;             // PairKey(heroA, heroB)
        [SaveableField(2)] private Dictionary<string, List<string>> _activeSuitorsPerTarget;   // targetId -> suitorIds
        [SaveableField(3)] private Dictionary<string, CampaignTime> _lastRivalryTick;          // PairKey(a,b) unordered
        [SaveableField(4)] private Dictionary<string, CampaignTime> _lastClanProcess;          // clanId -> last time we processed romance

        public NpcRomanceCampaignBehavior()
        {
            _lastApproach = new Dictionary<string, CampaignTime>();
            _activeSuitorsPerTarget = new Dictionary<string, List<string>>();
            _lastRivalryTick = new Dictionary<string, CampaignTime>();
            _lastClanProcess = new Dictionary<string, CampaignTime>();
        }

        public override void RegisterEvents()
        {
            CampaignEvents.DailyTickClanEvent.AddNonSerializedListener(this, OnDailyTickClan);
            CampaignEvents.HeroKilledEvent.AddNonSerializedListener(this, OnHeroKilled);
            CampaignEvents.WarDeclared.AddNonSerializedListener(this, OnWarDeclared);
            CampaignEvents.DailyTickEvent.AddNonSerializedListener(this, OnDailyTickGlobal);
        }

        public override void SyncData(IDataStore dataStore)
        {
            try
            {
                // ---- _lastApproach : Dictionary<string, CampaignTime>  -> (keys, valuesDays)
                var laKeys = new List<string>();
                var laValsDays = new List<float>();
                if (dataStore.IsSaving)
                {
                    foreach (var kv in _lastApproach)
                    {
                        laKeys.Add(kv.Key);
                        laValsDays.Add((float)kv.Value.ToDays);
                    }
                }
                dataStore.SyncData("npcRomance_lastApproach_keys", ref laKeys);
                dataStore.SyncData("npcRomance_lastApproach_valsDays", ref laValsDays);
                if (dataStore.IsLoading)
                {
                    _lastApproach = new Dictionary<string, CampaignTime>(laKeys.Count);
                    for (int i = 0; i < Math.Min(laKeys.Count, laValsDays.Count); i++)
                        _lastApproach[laKeys[i]] = CampaignTime.Days(laValsDays[i]);
                }

                // ---- _activeSuitorsPerTarget : Dictionary<string, List<string>> packed flat
                var tTargets = new List<string>();
                var tCounts = new List<int>();
                var tSuitorsFlat = new List<string>();
                if (dataStore.IsSaving)
                {
                    foreach (var kv in _activeSuitorsPerTarget)
                    {
                        tTargets.Add(kv.Key);
                        var list = kv.Value ?? new List<string>();
                        tCounts.Add(list.Count);
                        if (list.Count > 0) tSuitorsFlat.AddRange(list);
                    }
                }
                dataStore.SyncData("npcRomance_targets", ref tTargets);
                dataStore.SyncData("npcRomance_counts", ref tCounts);
                dataStore.SyncData("npcRomance_suitorsFlat", ref tSuitorsFlat);
                if (dataStore.IsLoading)
                {
                    _activeSuitorsPerTarget = new Dictionary<string, List<string>>(tTargets.Count);
                    int idx = 0;
                    for (int i = 0; i < tTargets.Count; i++)
                    {
                        int cnt = (i < tCounts.Count) ? tCounts[i] : 0;
                        var list = new List<string>(cnt);
                        for (int j = 0; j < cnt && idx < tSuitorsFlat.Count; j++, idx++)
                        {
                            var sid = tSuitorsFlat[idx];
                            if (!string.IsNullOrEmpty(sid)) list.Add(sid);
                        }
                        _activeSuitorsPerTarget[tTargets[i]] = list;
                    }
                }

                // ---- _lastRivalryTick : Dictionary<string, CampaignTime>  -> (keys, valuesDays)
                var rtKeys = new List<string>();
                var rtValsDays = new List<float>();
                if (dataStore.IsSaving)
                {
                    foreach (var kv in _lastRivalryTick)
                    {
                        rtKeys.Add(kv.Key);
                        rtValsDays.Add((float)kv.Value.ToDays);
                    }
                }
                dataStore.SyncData("npcRomance_lastRivalryTick_keys", ref rtKeys);
                dataStore.SyncData("npcRomance_lastRivalryTick_valsDays", ref rtValsDays);
                if (dataStore.IsLoading)
                {
                    _lastRivalryTick = new Dictionary<string, CampaignTime>(rtKeys.Count);
                    for (int i = 0; i < Math.Min(rtKeys.Count, rtValsDays.Count); i++)
                        _lastRivalryTick[rtKeys[i]] = CampaignTime.Days(rtValsDays[i]);
                }

                // ---- _lastClanProcess : Dictionary<string, CampaignTime>  -> (keys, valuesDays)
                var cpKeys = new List<string>();
                var cpValsDays = new List<float>();
                if (dataStore.IsSaving)
                {
                    foreach (var kv in _lastClanProcess)
                    {
                        cpKeys.Add(kv.Key);
                        cpValsDays.Add((float)kv.Value.ToDays);
                    }
                }
                dataStore.SyncData("npcRomance_lastClanProcess_keys", ref cpKeys);
                dataStore.SyncData("npcRomance_lastClanProcess_valsDays", ref cpValsDays);
                if (dataStore.IsLoading)
                {
                    _lastClanProcess = new Dictionary<string, CampaignTime>(cpKeys.Count);
                    for (int i = 0; i < Math.Min(cpKeys.Count, cpValsDays.Count); i++)
                        _lastClanProcess[cpKeys[i]] = CampaignTime.Days(cpValsDays[i]);
                }
            }
            catch (Exception ex)
            {
                if (DEBUG) NSLog.Log("[NPC-ROMANCE] SyncData ERROR: " + ex);
            }
        }

        // ======= EVENT HANDLERS =======
        private void OnDailyTickClan(Clan clan)
        {
            if (clan == Clan.PlayerClan || clan.IsEliminated) return;

            // gate: process each clan only every ClanProcessIntervalDays
            if (_lastClanProcess.TryGetValue(clan.StringId, out var last)
                && last.ElapsedDaysUntilNow < ClanProcessIntervalDays)
                return;
            _lastClanProcess[clan.StringId] = CampaignTime.Now;

            int acted = 0;
            int marriages = 0;
            int advancesToCompat = 0;
            int courtshipStarts = 0;

            foreach (var a in clan.Lords.ToList())
            {
                if (!IsNpcFreeToCourt(a)) continue;

                var b = PickOrContinueTargetFor(a);
                if (b == null) continue;

                var level = Romance.GetRomanticLevel(a, b);

                if (level == Romance.RomanceLevelEnum.Untested)
                {
                    if (CooldownOk(a, b))
                    {
                        ChangeRomanticStateAction.Apply(a, b, Romance.RomanceLevelEnum.CourtshipStarted);
                        RegisterSuitor(b, a);
                        TouchCooldown(a, b);
                        acted++; courtshipStarts++;
                        Log($"CourtshipStarted: {H(a)} began courting {H(b)}");
                        CourtshipTelemetry.MarkCourtshipStart(a, b);
                    }
                }
                else if (level == Romance.RomanceLevelEnum.CourtshipStarted)
                {
                    if (CooldownOk(a, b))
                    {
                        if (TryAdvance(a, b, Romance.RomanceLevelEnum.CoupleDecidedThatTheyAreCompatible))
                        {
                            TouchCooldown(a, b);
                            acted++; advancesToCompat++;
                            Log($"Advanced: {H(a)} & {H(b)} → CoupleDecidedThatTheyAreCompatible");
                        }
                    }
                }
                else if (level == Romance.RomanceLevelEnum.CoupleDecidedThatTheyAreCompatible)
                {
                    if (CooldownOk(a, b))
                    {
                        if (TryAdvance(a, b, Romance.RomanceLevelEnum.CoupleAgreedOnMarriage))
                        {
                            TouchCooldown(a, b);
                            acted++;
                            Log($"Advanced: {H(a)} & {H(b)} → CoupleAgreedOnMarriage");
                        }
                    }
                }
                else if (level == Romance.RomanceLevelEnum.CoupleAgreedOnMarriage)
                {
                    if (FinalChecksPass(a, b))
                    {
                        MarriageAction.Apply(a, b, showNotification: false);
                        ApplyRivalryFalloutOnSuccess(b, a);
                        ClearRivals(b);
                        acted++; marriages++;
                        Log($"MARRIAGE: {H(a)} married {H(b)}");
                        CourtshipTelemetry.MarkMarriage(a, b);
                    }
                }

                TickRivalries(b); // rivalry penalties (gated)
            }

            if (acted > 0)
                Log($"Clan {C(clan)} completed {acted} romance actions today (starts={courtshipStarts}, compat={advancesToCompat}, marriages={marriages}, every {ClanProcessIntervalDays}d).");
        }

        private void OnHeroKilled(Hero victim, Hero killer, KillCharacterAction.KillCharacterActionDetail detail, bool showNotification)
        {
            if (victim == null) return;

            int removedTargets = _activeSuitorsPerTarget.Remove(victim.StringId) ? 1 : 0;

            int removedSuitors = 0;
            foreach (var k in _activeSuitorsPerTarget.Keys.ToList())
            {
                if (_activeSuitorsPerTarget[k].Remove(victim.StringId)) removedSuitors++;
                if (_activeSuitorsPerTarget[k].Count == 0)
                    _activeSuitorsPerTarget.Remove(k);
            }

            int removedCooldowns = _lastApproach.Keys.Count(k => k.Contains(victim.StringId));
            foreach (var key in _lastApproach.Keys.Where(k => k.Contains(victim.StringId)).ToList())
                _lastApproach.Remove(key);

            int removedRivalTicks = _lastRivalryTick.Keys.Count(k => k.Contains(victim.StringId));
            foreach (var key in _lastRivalryTick.Keys.Where(k => k.Contains(victim.StringId)).ToList())
                _lastRivalryTick.Remove(key);

            Log($"Cleanup on death {H(victim)}: targetsRemoved={removedTargets}, suitorRefsRemoved={removedSuitors}, cooldownsRemoved={removedCooldowns}, rivalryTicksRemoved={removedRivalTicks}");
            CourtshipTelemetry.ForgetHero(victim);
        }

        private void OnWarDeclared(IFaction f1, IFaction f2, DeclareWarAction.DeclareWarDetail detail)
        {
            Log($"War declared: {f1?.Name} vs {f2?.Name} ({detail}). Existing romances won’t advance if blocked by checks.");
        }

        private void OnDailyTickGlobal()
        {
            int targetsCleaned = 0, suitorsCleaned = 0;

            foreach (var targetId in _activeSuitorsPerTarget.Keys.ToList())
            {
                var target = Hero.FindFirst(h => h.StringId == targetId);
                if (target == null || !target.IsAlive || target.Spouse != null)
                {
                    _activeSuitorsPerTarget.Remove(targetId);
                    targetsCleaned++;
                    continue;
                }

                suitorsCleaned += _activeSuitorsPerTarget[targetId].RemoveAll(sid =>
                {
                    var s = Hero.FindFirst(h => h.StringId == sid);
                    return s == null || !s.IsAlive || s.Spouse != null;
                });

                if (_activeSuitorsPerTarget[targetId].Count == 0)
                    _activeSuitorsPerTarget.Remove(targetId);
            }

            if (targetsCleaned > 0 || suitorsCleaned > 0)
                Log($"Daily cleanup: removed {targetsCleaned} invalid targets, {suitorsCleaned} invalid suitors.");
        }

        // ======= CORE HELPERS =======
        private bool IsNpcFreeToCourt(Hero h)
        {
            if (h == null || !h.IsAlive) return false;
            if (!h.CanMarry() || h.Spouse != null || h.IsPrisoner) return false;

            var party = h.PartyBelongedTo;
            if (party != null && (party.Army != null || party.MapEvent != null)) return false;

            if (h.MapFaction == null || h.MapFaction.IsMinorFaction) return false;
            return true;
        }

        private Hero PickOrContinueTargetFor(Hero a)
        {
            // Continue current partner if already courting
            var existing = Romance.RomanticStateList
                .FirstOrDefault(rs => (rs.Person1 == a || rs.Person2 == a) &&
                                      rs.Level >= Romance.RomanceLevelEnum.CourtshipStarted &&
                                      rs.Level < Romance.RomanceLevelEnum.Marriage);
            if (existing != null)
            {
                var b = existing.Person1 == a ? existing.Person2 : existing.Person1;
                if (FinalChecksPass(a, b))
                    return b;
            }

            var model = Campaign.Current.Models.MarriageModel;

            // Build pool: only nobles who can marry, not at war, and HAVE MET recently.
            var pool = Clan.All
                .Where(c => IsClanSuitable(c) && model.ShouldNpcMarriageBetweenClansBeAllowed(a.Clan, c))
                .SelectMany(c => c.Lords)
                .Where(b => b != null && b != a
                            && IsNpcFreeToCourt(b)
                            && model.IsCoupleSuitableForMarriage(a, b)
                            && !FactionManager.IsAtWarAgainstFaction(a.MapFaction, b.MapFaction)
                            && MeetingTracker.HasMetWithinDays(a, b, 60f))
                .ToList();

            if (pool.Count == 0) return null;

            // Order by: fewest current suitors, then distance
            pool.Sort((x, y) =>
            {
                int sx = GetSuitorList(x).Count;
                int sy = GetSuitorList(y).Count;
                int cmp = sx.CompareTo(sy);
                if (cmp != 0) return cmp;
                float dx = DistanceBetween(a, x);
                float dy = DistanceBetween(a, y);
                return dx.CompareTo(dy);
            });

            foreach (var candidate in pool)
            {
                int active = GetSuitorList(candidate).Count;
                if (active < MaxConcurrentSuitors)
                    return candidate;
            }

            return null;
        }

        private static float DistanceBetween(Hero a, Hero b)
        {
            try
            {
                var m = Campaign.Current.Models.MapDistanceModel;
                var sa = a.HomeSettlement ?? a.Clan?.FactionMidSettlement;
                var sb = b.HomeSettlement ?? b.Clan?.FactionMidSettlement;
                if (sa != null && sb != null)
                    return m.GetDistance(sa, sb);
            }
            catch { }
            return float.MaxValue;
        }

        private bool IsClanSuitable(Clan c)
        {
            var model = Campaign.Current.Models.MarriageModel;
            return c != null && model.IsClanSuitableForMarriage(c);
        }

        private bool CooldownOk(Hero a, Hero b)
        {
            var key = PairKey(a, b);
            if (!_lastApproach.TryGetValue(key, out var t)) return true;
            return t.ElapsedDaysUntilNow >= RomanceCooldownDays;
        }

        private void TouchCooldown(Hero a, Hero b)
        {
            var key = PairKey(a, b);
            _lastApproach[key] = CampaignTime.Now;
            // (verbose-only; suppressed by default)
            if (LOG_VERBOSE) Log($"Cooldown touched: {H(a)} ↔ {H(b)} next action after +{RomanceCooldownDays:0.#}d");
        }

        private bool TryAdvance(Hero a, Hero b, Romance.RomanceLevelEnum to)
        {
            if (!FinalChecksPass(a, b))
                return false;

            float p = ComputeAdvanceChance(a, b);
            float roll = MBRandom.RandomFloat;

            // (verbose-only)
            if (LOG_VERBOSE) Log($"Advance try {H(a)} & {H(b)} → {to} | p={p:0.000} roll={roll:0.000}");

            if (roll < p)
            {
                ChangeRomanticStateAction.Apply(a, b, to);
                return true;
            }
            else
            {
                float failRoll = MBRandom.RandomFloat;
                if (failRoll < 0.15f)
                {
                    var fail = (to == Romance.RomanceLevelEnum.CoupleDecidedThatTheyAreCompatible)
                        ? Romance.RomanceLevelEnum.FailedInCompatibility
                        : Romance.RomanceLevelEnum.FailedInPracticalities;
                    ChangeRomanticStateAction.Apply(a, b, fail);
                    UnregisterSuitor(b, a);
                    if (LOG_VERBOSE) Log($"Hard fail applied to {H(a)} & {H(b)} → {fail}");
                }
            }
            return false;
        }

        private float ComputeAdvanceChance(Hero a, Hero b)
        {
            float p = BaseAdvanceChance;

            try
            {
                int attraction = Campaign.Current.Models.RomanceModel.GetAttractionValuePercentage(b, a);
                float deltaRaw = (attraction - 50) / 200f;     // -0.25..+0.25
                float delta = Clamp(deltaRaw, -0.15f, 0.15f);  // clamp to ±0.15
                p += delta;
                if (LOG_VERBOSE) Log($"  Chance +attraction({attraction}%) = {delta:+0.00;-0.00} (raw {deltaRaw:+0.00;-0.00})");
            }
            catch { }

            try
            {
                int rel = a.Clan.GetRelationWithClan(b.Clan);
                float delta = Clamp(rel / 100f, -0.2f, 0.2f);
                p += delta;
                if (LOG_VERBOSE) Log($"  Chance +clanRel({rel}) = {delta:+0.00;-0.00}");
            }
            catch { }

            if (a.Clan?.Kingdom != null && a.Clan.Kingdom == b.Clan?.Kingdom)
            {
                p += 0.1f;
                if (LOG_VERBOSE) Log("  Chance +sameKingdom = +0.10");
            }

            int fiefs = Settlement.All.Count(s => s.OwnerClan == a.Clan);
            if (fiefs >= 1) { p += 0.05f; if (LOG_VERBOSE) Log("  Chance +fiefs>=1 = +0.05"); }
            if (fiefs >= 3) { p += 0.05f; if (LOG_VERBOSE) Log("  Chance +fiefs>=3 = +0.05"); }

            float clamped = Clamp(p, 0.05f, 0.90f);
            if (LOG_VERBOSE && Math.Abs(clamped - p) > 0.0001f)
                Log($"  Chance clamped to {clamped:0.000}");
            return clamped;
        }

        private bool FinalChecksPass(Hero a, Hero b)
        {
            if (a == null || b == null) return false;
            if (!a.IsAlive || !b.IsAlive) return false;
            if (a.Spouse != null || b.Spouse != null) return false;
            if (!IsNpcFreeToCourt(a) || !IsNpcFreeToCourt(b)) return false;
            if (FactionManager.IsAtWarAgainstFaction(a.MapFaction, b.MapFaction)) return false;

            var model = Campaign.Current.Models.MarriageModel;
            if (!model.IsCoupleSuitableForMarriage(a, b)) return false; // includes blood-family checks
            if (!model.ShouldNpcMarriageBetweenClansBeAllowed(a.Clan, b.Clan)) return false;
            return true;
        }

        // ======= RIVALRY =======
        private void RegisterSuitor(Hero target, Hero suitor)
        {
            var list = GetSuitorList(target);
            if (!list.Contains(suitor.StringId))
                list.Add(suitor.StringId);
            if (LOG_VERBOSE) Log($"Register suitor: {H(suitor)} for {H(target)} | total suitors now={list.Count}");
        }

        private void UnregisterSuitor(Hero target, Hero suitor)
        {
            if (target == null) return;
            if (_activeSuitorsPerTarget.TryGetValue(target.StringId, out var list) && list.Remove(suitor.StringId))
                if (LOG_VERBOSE) Log($"Unregister suitor: {H(suitor)} from {H(target)} | total suitors now={list.Count}");
            if (list != null && list.Count == 0)
                _activeSuitorsPerTarget.Remove(target.StringId);
        }

        private List<string> GetSuitorList(Hero target)
        {
            if (target == null) return new List<string>();
            if (!_activeSuitorsPerTarget.TryGetValue(target.StringId, out var list))
            {
                list = new List<string>();
                _activeSuitorsPerTarget[target.StringId] = list;
            }
            return list;
        }

        private void TickRivalries(Hero target)
        {
            if (target == null) return;
            if (!_activeSuitorsPerTarget.TryGetValue(target.StringId, out var suitors)) return;
            if (suitors.Count <= 1) return;

            var list = suitors.Select(id => Hero.FindFirst(h => h.StringId == id))
                              .Where(h => h != null && h.IsAlive)
                              .ToList();

            int pairs = 0;
            for (int i = 0; i < list.Count; i++)
                for (int j = i + 1; j < list.Count; j++)
                {
                    var a = list[i];
                    var b = list[j];
                    var key = PairKey(a, b); // unordered key for pair

                    if (!_lastRivalryTick.TryGetValue(key, out var t) || t.ElapsedDaysUntilNow >= RivalryTickDays)
                    {
                        ChangeRelationAction.ApplyRelationChangeBetweenHeroes(a, b, RivalryPairPenalty, showQuickNotification: false);
                        _lastRivalryTick[key] = CampaignTime.Now;
                        pairs++;
                        if (LOG_VERBOSE) Log($"Rivalry tick: {H(a)} ↔ {H(b)} over {H(target)} | applied {RivalryPairPenalty}");
                    }
                }

            if (pairs > 0)
                Log($"Rivalry tick summary for {H(target)}: affected pairs={pairs}");
        }

        private void ApplyRivalryFalloutOnSuccess(Hero target, Hero winningSuitor)
        {
            if (target == null || winningSuitor == null) return;
            if (!_activeSuitorsPerTarget.TryGetValue(target.StringId, out var suitors)) return;

            int affected = 0;
            foreach (var sid in suitors)
            {
                if (sid == winningSuitor.StringId) continue;
                var rival = Hero.FindFirst(h => h.StringId == sid);
                if (rival == null || !rival.IsAlive) continue;

                ChangeRelationAction.ApplyRelationChangeBetweenHeroes(winningSuitor, rival, -10, false);
                affected++;
                if (LOG_VERBOSE) Log($"Rivalry fallout: {H(winningSuitor)} vs {H(rival)} (married {H(target)}) applied -10");
            }

            if (affected > 0)
                Log($"Rivalry fallout summary for {H(target)} married to {H(winningSuitor)}: rivals affected={affected}");
        }

        private void ClearRivals(Hero target)
        {
            if (target == null) return;
            _activeSuitorsPerTarget.Remove(target.StringId);
            if (LOG_VERBOSE) Log($"Cleared rivals for {H(target)}");
        }

        // ======= KEYS =======
        private static string PairKey(Hero a, Hero b)
        {
            var x = a.StringId;
            var y = b.StringId;
            return (string.CompareOrdinal(x, y) < 0) ? (x + "|" + y) : (y + "|" + x);
        }
    }
}
