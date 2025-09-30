// NobleSociety/Patches/OnAgentRemovedPatch.cs
using System;
using System.Collections.Generic;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CharacterDevelopment;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.MountAndBlade;

namespace NobleSociety.Patches
{
    /// <summary>
    /// Mirrors HealthyRelationshipsRewrite's player-centric OnAgentRemoved relation logic
    /// AND extends it to NPC-vs-NPC noble bouts during TOURNAMENT missions only.
    ///
    /// - Requires valid KillingBlow and both sides to resolve to Hero.
    /// - Skips same-team hits.
    /// - De-dupe per mission:
    ///     * Player branches: per "loser Agent" (affectedAgents).
    ///     * NPC-vs-NPC tournament branch: per (winner -> loser) hero pair (processedPairs).
    /// - "Positivity" = Honor + Valor + Generosity + Mercy >= 0.
    /// - Toasts: only for player-involved branches (bottom-left).
    /// - No ripple triggers here; your ApplyInternal patch already suppresses < 10.
    /// </summary>
    [HarmonyPatch(typeof(Mission), "OnAgentRemoved")]
    internal static class OnAgentRemovedPatch
    {
        // Per-mission dedupe for PLAYER-involved events (cleared externally)
        internal static readonly List<Agent> affectedAgents = new List<Agent>(64);

        // Per-tournament dedupe for NPC-vs-NPC (winner->loser hero StringId key)
        private static readonly HashSet<string> processedPairs = new HashSet<string>();

        // === Simple settings (no MCM) ===
        private const bool battleRelationsPopupEnabled = true;

        private const int battleDefeatedByPositivePlayerRelationGain = 2; // when player defeats a positive hero
        private const int battleDefeatedByNegativePlayerRelationLost = 2; // when player defeats a negative hero
        private const int battlePositivePlayerDefeatedRelationGain = 1; // when player is defeated by a positive hero
        private const int battleNegativePlayerDefeatedRelationLost = 1; // when player is defeated by a negative hero

        // Toast colors (match HRR vibe)
        private const uint ColorGain = 6750105u;   // greenish
        private const uint ColorLoss = 16722716u;  // reddish

        /// <summary>
        /// Called by NobleMemoryEventsBehavior after MapEventEnded and TournamentFinished.
        /// </summary>
        public static void ClearDedupe()
        {
            if (affectedAgents != null && affectedAgents.Count > 0)
                affectedAgents.Clear();

            if (processedPairs != null && processedPairs.Count > 0)
                processedPairs.Clear();
        }

        // Postfix signature: (Agent affectedAgent, Agent affectorAgent, AgentState agentState, KillingBlow killingBlow)
        static void Postfix(Agent affectedAgent, Agent affectorAgent, AgentState agentState, KillingBlow killingBlow)
        {
            try
            {
                // Basic guards
                if (affectedAgent == null || affectorAgent == null)
                    return;

                var affectedChar = affectedAgent.Character;
                var affectorChar = affectorAgent.Character;
                if (affectedChar == null || affectorChar == null)
                    return;

                // Require at least one to be Hero (we’ll resolve to Hero objects next)
                if (!affectorChar.IsHero && !affectedChar.IsHero)
                    return;

                // Same team / friendly fire ignored
                if (affectedAgent.Team != null && affectorAgent.Team != null &&
                    affectedAgent.Team == affectorAgent.Team)
                    return;

                // Require a valid combat removal (like HRR)
                if (!killingBlow.IsValid)
                    return;

                // Resolve to Hero objects by StringId
                var winner = Hero.FindFirst(x => x != null && x.StringId == affectorChar.StringId);
                var loser = Hero.FindFirst(x => x != null && x.StringId == affectedChar.StringId);

                if (winner == null || loser == null || winner == loser)
                    return;

                // =========================
                // PLAYER-INVOLVED BRANCHES
                // =========================
                // De-dupe per "loser agent" per mission only for player-involved cases
                if (winner == Hero.MainHero || loser == Hero.MainHero)
                {
                    if (affectedAgents.Contains(affectedAgent))
                        return;

                    bool winnerIsPositive = IsPositivePerson(winner);
                    bool loserIsPositive = IsPositivePerson(loser);

                    int delta = 0;
                    string toast = null;
                    uint toastColor = ColorGain;

                    // Player defeats a hero
                    if (winner == Hero.MainHero)
                    {
                        if (loserIsPositive)
                        {
                            delta = battleDefeatedByPositivePlayerRelationGain;
                            if (battleRelationsPopupEnabled)
                            {
                                var t = new TextObject("{=battle_defeatedByPositivePlayer}{LORD_NAME} will remember your Valor in battle!");
                                t.SetTextVariable("LORD_NAME", loser.Name);
                                toast = t.ToString();
                                toastColor = ColorGain;
                            }
                        }
                        else
                        {
                            delta = -battleDefeatedByNegativePlayerRelationLost;
                            if (battleRelationsPopupEnabled)
                            {
                                var t = new TextObject("{=battle_defeatedByNegativePlayer}{LORD_NAME} will be frustrated with this defeat!");
                                t.SetTextVariable("LORD_NAME", loser.Name);
                                toast = t.ToString();
                                toastColor = ColorLoss;
                            }
                        }

                        if (delta != 0)
                            ChangeRelationAction.ApplyRelationChangeBetweenHeroes(Hero.MainHero, loser, delta, false);
                    }
                    // Player is defeated by a hero
                    else // loser == Hero.MainHero
                    {
                        if (winnerIsPositive)
                        {
                            delta = battlePositivePlayerDefeatedRelationGain;
                            if (battleRelationsPopupEnabled)
                            {
                                var t = new TextObject("{=battle_defeatedByPositiveLord}{LORD_NAME} respects your strength in battle.");
                                t.SetTextVariable("LORD_NAME", winner.Name);
                                toast = t.ToString();
                                toastColor = ColorGain;
                            }
                        }
                        else
                        {
                            delta = -battleNegativePlayerDefeatedRelationLost;
                            if (battleRelationsPopupEnabled)
                            {
                                var t = new TextObject("{=battle_defeatedByNegativeLord}{LORD_NAME} taunts you as you fall to the ground.");
                                t.SetTextVariable("LORD_NAME", winner.Name);
                                toast = t.ToString();
                                toastColor = ColorLoss;
                            }
                        }

                        if (delta != 0)
                            ChangeRelationAction.ApplyRelationChangeBetweenHeroes(Hero.MainHero, winner, delta, false);
                    }

                    if (!string.IsNullOrEmpty(toast))
                        InformationManager.DisplayMessage(new InformationMessage(toast, Color.FromUint(toastColor)));

                    affectedAgents.Add(affectedAgent);
                    return;
                }

                // ================================
                // NPC vs NPC: TOURNAMENTS ONLY
                // ================================
                if (!IsTournamentMission())
                    return;

                // Nobles (lords) only
                if (!winner.IsLord || !loser.IsLord)
                    return;

                // Per-tournament dedupe by hero pair (winner -> loser)
                string pairKey = winner.StringId + "|" + loser.StringId;
                if (processedPairs.Contains(pairKey))
                    return;

                // Apply the same positivity rule/magnitudes as "player defeats positive/negative"
                bool loserIsPositiveNpc = IsPositivePerson(loser);
                int npcDelta = loserIsPositiveNpc
                    ? battleDefeatedByPositivePlayerRelationGain
                    : -battleDefeatedByNegativePlayerRelationLost;

                if (npcDelta != 0)
                    ChangeRelationAction.ApplyRelationChangeBetweenHeroes(winner, loser, npcDelta, false);

                processedPairs.Add(pairKey);
                // No toasts for NPC-vs-NPC to avoid spam
            }
            catch
            {
                // Swallow exceptions like HRR's try/catch to avoid disrupting missions
            }
        }

        private static bool IsPositivePerson(Hero hero)
        {
            // Match HRR: sum of Honor + Valor + Generosity + Mercy >= 0
            int score =
                hero.GetTraitLevel(DefaultTraits.Honor) +
                hero.GetTraitLevel(DefaultTraits.Valor) +
                hero.GetTraitLevel(DefaultTraits.Generosity) +
                hero.GetTraitLevel(DefaultTraits.Mercy);

            return score >= 0;
        }

        private static bool IsTournamentMission()
        {
            try
            {
                var mission = Mission.Current;
                if (mission == null)
                    return false;

                // Broadly compatible tournament detection
                return mission.Mode == MissionMode.Tournament;
            }
            catch
            {
                return false;
            }
        }
    }
}
