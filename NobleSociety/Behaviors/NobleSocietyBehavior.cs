using System;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using NobleSociety.State;
using NobleSociety.Logging;

namespace NobleSociety.Behaviors
{
    /// <summary>
    /// Core daily driver for Noble Society AI plus memory‑decay housekeeping.
    /// Adds seasonal prune‑metrics logging and a light per‑tag memory cap.
    /// </summary>
    public class NobleSocietyBehavior : CampaignBehaviorBase
    {
        // ---- Seasonal logging ------------------------------------------------
        private const int DAYS_PER_SEASON = 10;          // TimeLord calendar (40 days / year)
        private int _seasonAnchorDay = -1;
        private int _seasonPrunedMemories;
        private int _seasonPrunedGossip;

        // Per‑tag cap (cross‑faction scaling) ----------------------------------
        // Per‑tag cap (favor high‑value memories)
        private readonly Dictionary<MemoryTag, int> _tagCaps = new Dictionary<MemoryTag, int>
        {
            // ⚔️ Military history stays longest
            { MemoryTag.BattleVictory, 180 },
            { MemoryTag.BattleDefeat,  180 },
            // 🏰 Enduring political beliefs / betrayals
            { MemoryTag.Belief,        120 },
            { MemoryTag.Betrayal,      120 },
            // 📜 Trade & diplomacy – moderately important
            { MemoryTag.TradeAgreement, 80 },
            // 🗣️ Everyday rumours fade fastest
            { MemoryTag.Gossip,         60 }
        };

        public override void RegisterEvents()
        {
            CampaignEvents.DailyTickHeroEvent.AddNonSerializedListener(this, OnDailyTickHero);
        }

        public override void SyncData(IDataStore dataStore) { }

        // ----------------------------------------------------------------------
        private void OnDailyTickHero(Hero hero)
        {
            if (!hero.IsLord || hero.IsChild || hero.IsDead) return;

            var agent = NobleSocietyManager.GetOrCreateAgent(hero);

            // Run per‑hero AI
            agent.Tick();

            // Memory housekeeping (returns pruned counts)
            var (pruned, gossipPruned) = PruneAndDecay(agent);
            _seasonPrunedMemories += pruned;
            _seasonPrunedGossip += gossipPruned;

            // Seasonal checkpoint (every 10 accelerated days)
            int day = (int)Math.Floor(CampaignTime.Now.ToDays);
            if (_seasonAnchorDay < 0) _seasonAnchorDay = day;

            if (day - _seasonAnchorDay >= DAYS_PER_SEASON)
            {
                FileLogger.Log($"[SEASON SUMMARY] Pruned { _seasonPrunedMemories } memories, { _seasonPrunedGossip } gossip entries.");
                _seasonPrunedMemories = _seasonPrunedGossip = 0;
                _seasonAnchorDay = day; // reset anchor
            }
        }

        // ----------------------------------------------------------------------
        private (int prunedMemories, int prunedGossip) PruneAndDecay(NobleAgentState agent)
        {
            const int HALF_LIFE_DAYS = 27;  // accelerated days
            const float CULL_THRESHOLD = 0.20f;
            const int HARD_CAP = 250;

            float daysSinceLast = (float)CampaignTime.Now.ToDays - agent.LastDecayDay;
            if (daysSinceLast < 1f) return (0, 0);    // run once per in‑game day

            // ---- 1️⃣ exponential decay ---------------------------------------
            float decayMult = (float)Math.Pow(0.5, daysSinceLast / HALF_LIFE_DAYS);
            foreach (var m in agent.MemoryLog)
                m.Weight *= decayMult;

            // ---- 2️⃣ threshold cull & per‑tag cap ----------------------------
            int removed = agent.MemoryLog.RemoveAll(m => m.Weight < CULL_THRESHOLD);

            // Enforce per‑tag cap (cross‑faction scaling)
            foreach (var kvp in _tagCaps)
            {
                var list = agent.MemoryLog.FindAll(m => m.Tags.Contains(kvp.Key));
                if (list.Count <= kvp.Value) continue;
                int toDrop = list.Count - kvp.Value;
                // Drop oldest of that tag
                list.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
                for (int i = 0; i < toDrop; i++)
                    agent.MemoryLog.Remove(list[i]);
                removed += toDrop;
            }

            // ---- 3️⃣ hard cap FIFO -------------------------------------------
            if (agent.MemoryLog.Count > HARD_CAP)
            {
                int toRemove = agent.MemoryLog.Count - HARD_CAP;
                agent.MemoryLog.RemoveRange(0, toRemove);
                removed += toRemove;
            }

            agent.LastDecayDay = (float)CampaignTime.Now.ToDays;

            // No per‑agent gossip list yet; return 0 for gossip until implemented.
            return (removed, 0);
        }
    }
}
