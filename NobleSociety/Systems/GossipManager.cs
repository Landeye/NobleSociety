using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using System.Collections.Generic;
using System.Linq;
using NobleSociety.State;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.CampaignSystem.Actions;
using RealisticEconomy.Models;
using NSLog = NobleSociety.Logging.FileLogger;
using TaleWorlds.CampaignSystem.CharacterDevelopment;

namespace NobleSociety.Systems
{
    public static class GossipManager
    {
        private static List<GossipEvent> _gossipLog = new List<GossipEvent>();
        private const float GossipLifespanDays = 20f;
        private static Dictionary<(Hero, Hero), CampaignTime> _lastInteractionTime = new Dictionary<(Hero, Hero), CampaignTime>();

        // ===== Balance & anti-drift config =====
        private const float WeeklyPairDeltaCap = 2f;        // Max absolute relation change per listener↔actor per 7 days from gossip
        private const int MinDaysBetweenSameReason = 5;   // Cooldown for same pair+reason
        private const bool RequireBeliefForRelation = false; // Set true if you want relation changes ONLY after belief promotion

        // Rolling ledgers (not persisted across save unless you add SyncData here)
        private static readonly Dictionary<(Hero, Hero), (CampaignTime weekStart, float delta)> _weeklyPairLedger
            = new Dictionary<(Hero, Hero), (CampaignTime, float)>();
        private static readonly Dictionary<(Hero, Hero, string), CampaignTime> _lastReasonTime
            = new Dictionary<(Hero, Hero, string), CampaignTime>();

        public static void SpreadGossip(Hero speaker, Hero subject, string topic, string message, Settlement location = null)
        {
            GossipEvent gossip = new GossipEvent(speaker, subject, topic, message, location);
            _gossipLog.Add(gossip);
        }

        public static List<GossipEvent> GetGossipAbout(Hero subject) =>
            _gossipLog.Where(g => g.Subject == subject).ToList();

        public static List<GossipEvent> GetRecentGossip(CampaignTime since) =>
            _gossipLog.Where(g => g.Timestamp > since).ToList();

        public static void TryGossip(NobleAgentState agent)
        {
            CleanupOldGossip();

            if (agent.MemoryLog.Count == 0)
                return;

            Hero speaker = agent.Hero;
            if (speaker.IsDead || speaker.IsChild || !speaker.IsLord)
                return;

            List<Hero> nearbyNobles = GetNearbyNobles(speaker);
            if (nearbyNobles.Count == 0)
                return;

            var candidateMemories = agent.MemoryLog
                .Where(m =>
                    !m.IsExpired(agent) &&
                    (m.Type != MemoryType.GossipHeard || (m.RepeatCount >= 3 && m.Tags.Contains(MemoryTag.Gossip)))
                )
                .OrderByDescending(m =>
                    m.Weight * GetTraitAffinity(agent.Hero, m.Type) /
                    (CampaignTime.Now.ToDays - m.Timestamp.ToDays + 1))
                .ToList();

            if (candidateMemories.Count == 0)
                return;

            var memory = candidateMemories.First();
            if (memory == null)
                return;

            Hero listener = nearbyNobles.GetRandomElement();
            var key = (speaker, listener);
            var reverseKey = (listener, speaker);
            if ((_lastInteractionTime.TryGetValue(key, out var lastTime) && CampaignTime.Now.ToDays - lastTime.ToDays < 1) ||
                (_lastInteractionTime.TryGetValue(reverseKey, out var lastTime2) && CampaignTime.Now.ToDays - lastTime2.ToDays < 1))
                return;

            var listenerAgent = NobleSocietyManager.GetOrCreateAgent(listener);
            // Short-circuit if listener already fully believes this exact memory
            if (listenerAgent.MemoryLog.Any(m =>
                m.Type == memory.Type &&
                m.Target == memory.Target &&
                m.Notes == memory.Notes))
                return;

            SpreadGossip(speaker, memory.Target, memory.Type.ToString(), memory.Notes, speaker.CurrentSettlement);

            // 🔍 Updated search logic: fallback to Type if OriginalType is null
            var existing = listenerAgent.MemoryLog.FirstOrDefault(m =>
                m.Type == MemoryType.GossipHeard &&
                (m.OriginalType == memory.Type || m.Type == memory.Type) &&
                m.Notes == memory.Notes &&
                m.Target == memory.Target);

            bool beliefJustPromoted = false;

            if (existing != null)
            {
                existing.RepeatCount = MathF.Min(existing.RepeatCount + 1, 5);
                existing.Weight += 0.05f;

                int trustFactor = speaker.GetRelation(listener);
                float traitAffinity = GetTraitAffinity(listener, memory.Type);

                //NSLog.Log($"[DEBUG] {listener.Name} has heard gossip about {memory.Type} {existing.RepeatCount} times (trust={trustFactor}, trait={traitAffinity:0.00})");

                int valor = listener.GetTraitLevel(DefaultTraits.Valor);
                int calc = listener.GetTraitLevel(DefaultTraits.Calculating);
                int honor = listener.GetTraitLevel(DefaultTraits.Honor);

                float beliefScore = 0f;
                beliefScore += trustFactor / 10f;         // +1 at +10 relation
                beliefScore += traitAffinity - 1f;        // trait alignment boost
                beliefScore += valor * 0.3f;              // brave = more trusting of instinct
                beliefScore += calc * -0.2f;              // calculating = skeptical
                if (memory.Type == MemoryType.Murder || memory.Type == MemoryType.Betrayal)
                    beliefScore += honor * 0.3f + 0.5f;   // justice/dark-rumor bias
                if (memory.Type == MemoryType.TradeDeal || memory.Type == MemoryType.MinorFavor)
                    beliefScore -= 0.2f;                 // low-stakes discount

                bool isAlly = listener.Clan == memory.Target?.Clan;
                bool isNegative = memory.Type == MemoryType.Murder || memory.Type == MemoryType.Betrayal || memory.Type == MemoryType.BattleDefeat;
                if (isAlly && isNegative) beliefScore -= 0.5f; // ignore smear vs allies

                if (existing.RepeatCount >= 2 && beliefScore >= 1.0f)
                {
                    var promoted = new NobleMemoryEntry(
                        type: memory.Type,
                        source: speaker,
                        target: memory.Target,
                        weight: memory.Weight * 0.5f,
                        notes: memory.Notes
                    );
                    listenerAgent.MemoryLog.Add(promoted);
                    beliefJustPromoted = true;
                    //NSLog.Log($"[BELIEF] {listener.Name} now believes gossip about {memory.Type} from {speaker.Name} ({memory.Notes})");
                }
            }
            else
            {
                var gossipMemory = new NobleMemoryEntry(
                    type: MemoryType.GossipHeard,
                    source: speaker,
                    target: memory.Target,
                    weight: 0.1f,
                    notes: memory.Notes
                );
                gossipMemory.OriginalType = memory.Type;
                gossipMemory.Tags.Add(MemoryTag.Gossip);
                listenerAgent.MemoryLog.Add(gossipMemory);
            }

            string targetName = memory.Target != null ? memory.Target.Name?.ToString() : "unknown";
            //NSLog.Log($"[GOSSIP] {speaker.Name} gossiped to {listener.Name} about {memory.Type} involving {targetName} ({memory.Notes}) Weight: {memory.Weight:0.00}");

            // ===== Balanced relation effects =====
            var traits = listener.GetHeroTraits();
            var effectiveType = memory.OriginalType ?? memory.Type;

            // Optionally require belief before relations change
            if (RequireBeliefForRelation && !beliefJustPromoted)
            {
                _lastInteractionTime[key] = CampaignTime.Now;
                return;
            }

            switch (effectiveType)
            {
                case MemoryType.ReleasedAfterBattle:
                    if (traits.Mercy > 0)
                        TryApplyBalancedRel(listener, memory.Source, +1, "ReleasedAfterBattle+Mercy");
                    else if (traits.Mercy < 0)
                        TryApplyBalancedRel(listener, memory.Source, -1, "ReleasedAfterBattle-Mercy");
                    break;

                case MemoryType.BattleVictory:
                    if (traits.Valor > 0)
                        TryApplyBalancedRel(listener, memory.Source, +1, "BattleVictory+Valor");
                    if (traits.Calculating > 0)
                        TryApplyBalancedRel(listener, memory.Source, -1, "BattleVictory-Calculating");
                    break;

                case MemoryType.BattleDefeat:
                    if (traits.Calculating > 0)
                        TryApplyBalancedRel(listener, memory.Source, -1, "BattleDefeat-Calculating");
                    if (traits.Honor >= 2 && MBRandom.RandomFloat < 0.25f)
                        TryApplyBalancedRel(listener, memory.Source, +1, "BattleDefeat+Honor");
                    break;

                case MemoryType.LostSoldiersTo:
                    if (traits.Honor >= 2 && MBRandom.RandomFloat < 0.33f)
                        TryApplyBalancedRel(listener, memory.Source, +1, "LostSoldiersTo+Honor");
                    if (traits.Mercy >= 1 && MBRandom.RandomFloat < 0.33f)
                        TryApplyBalancedRel(listener, memory.Source, -1, "LostSoldiersTo-Mercy");
                    break;

                case MemoryType.Murder:
                    if (traits.Mercy > 0)
                        TryApplyBalancedRel(listener, memory.Source, -1, "Murder-Mercy");
                    break;

                case MemoryType.TradeDeal:
                    if (traits.Generosity > 2 && MBRandom.RandomFloat < 0.5f)
                        TryApplyBalancedRel(listener, speaker, +1, "TradeDeal+Generosity");
                    if (traits.Calculating > 1 && MBRandom.RandomFloat < 0.25f)
                        TryApplyBalancedRel(listener, speaker, -1, "TradeDeal-Calculating");
                    break;

                case MemoryType.MilitaryAid:
                case MemoryType.MinorFavor:
                    if (traits.Valor > 0 && MBRandom.RandomFloat < 0.66f)
                        TryApplyBalancedRel(listener, speaker, +1, "Aid+Valor");
                    if (traits.Honor < 0 && MBRandom.RandomFloat < 0.25f)
                        TryApplyBalancedRel(listener, speaker, -1, "Aid-Honor");
                    break;

                case MemoryType.Betrayal:
                    if (traits.Honor > 0)
                        TryApplyBalancedRel(listener, speaker, -1, "Betrayal-Honor");
                    if (traits.Calculating >= 2 && MBRandom.RandomFloat < 0.2f)
                        TryApplyBalancedRel(listener, speaker, +1, "Betrayal+Calculating");
                    break;

                case MemoryType.FavorRefused:
                    if (traits.Mercy < 0)
                        TryApplyBalancedRel(listener, speaker, +1, "FavorRefused+Mercy");
                    if (traits.Honor > 0 && MBRandom.RandomFloat < 0.5f)
                        TryApplyBalancedRel(listener, speaker, -1, "FavorRefused-Honor");
                    break;
            }

            _lastInteractionTime[key] = CampaignTime.Now;
        }

        // === Balanced relation applier ===
        private static bool TryApplyBalancedRel(Hero listener, Hero actor, int rawDelta, string reason)
        {
            if (listener == null || actor == null || rawDelta == 0) return false;

            var pair = (listener, actor);
            var rk = (listener, actor, reason);

            // per-reason cooldown
            if (_lastReasonTime.TryGetValue(rk, out var lastT) && (CampaignTime.Now.ToDays - lastT.ToDays) < MinDaysBetweenSameReason)
                return false;

            // reset or get weekly entry
            if (!_weeklyPairLedger.TryGetValue(pair, out var entry) || (CampaignTime.Now.ToDays - entry.weekStart.ToDays) >= 7f)
                entry = (CampaignTime.Now, 0f);

            float remaining = WeeklyPairDeltaCap - MathF.Abs(entry.delta);
            if (remaining <= 0f) { _weeklyPairLedger[pair] = entry; return false; }

            int applied = rawDelta;
            if (MathF.Abs(entry.delta + rawDelta) > WeeklyPairDeltaCap)
            {
                var room = WeeklyPairDeltaCap - MathF.Abs(entry.delta);
                applied = (int)MathF.Sign(rawDelta) * (int)MathF.Max(0f, room);
            }

            if (applied == 0) { _weeklyPairLedger[pair] = entry; return false; }

            ChangeRelationAction.ApplyRelationChangeBetweenHeroes(listener, actor, applied);
            entry.delta += applied;
            _weeklyPairLedger[pair] = entry;
            _lastReasonTime[rk] = CampaignTime.Now;
            return true;
        }

        private static void CleanupOldGossip()
        {
            CampaignTime threshold = CampaignTime.Now - CampaignTime.Days(GossipLifespanDays);
            _gossipLog.RemoveAll(g => g.Timestamp < threshold);
        }

        private static List<Hero> GetNearbyNobles(Hero speaker)
        {
            List<Hero> nearbyNobles = new List<Hero>();

            if (speaker.CurrentSettlement != null)
            {
                nearbyNobles.AddRange(speaker.CurrentSettlement.HeroesWithoutParty
                    .Where(h => h != speaker && h.IsLord && !h.IsDead && !h.IsChild));
            }
            else if (speaker.PartyBelongedTo?.Army != null)
            {
                nearbyNobles.AddRange(
                    speaker.PartyBelongedTo.Army.Parties
                        .Where(p => p.LeaderHero != null && p.LeaderHero != speaker)
                        .Select(p => p.LeaderHero)
                );
            }
            else if (speaker.PartyBelongedTo != null)
            {
                nearbyNobles.AddRange(speaker.PartyBelongedTo.MemberRoster.GetTroopRoster()
                    .Where(trp => trp.Character?.HeroObject != null && trp.Character.HeroObject != speaker)
                    .Select(trp => trp.Character.HeroObject)
                    .Where(h => h.IsLord && !h.IsDead && !h.IsChild));
            }

            return nearbyNobles;
        }

        private static float GetTraitAffinity(Hero hero, MemoryType type)
        {
            var traits = hero.GetHeroTraits();

            switch (type)
            {
                case MemoryType.BattleVictory:
                case MemoryType.MilitaryAid:
                case MemoryType.LostSoldiersTo:
                case MemoryType.TournamentWin:
                    return 1f + 0.25f * traits.Valor;

                case MemoryType.Murder:
                case MemoryType.ReleasedAfterBattle:
                    return 1f + 0.25f * traits.Mercy;

                case MemoryType.Betrayal:
                case MemoryType.FavorRefused:
                    return 1f + 0.25f * traits.Honor;

                case MemoryType.TradeDeal:
                case MemoryType.MinorFavor:
                    return 0.5f + 0.15f * traits.Generosity; // NOTE: if Generosity is intended, ensure GetHeroTraits() exposes it; fallback used to avoid compile issues if renamed

                default:
                    return 1f;
            }
        }
    }
}
