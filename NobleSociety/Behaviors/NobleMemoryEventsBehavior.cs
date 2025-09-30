using HarmonyLib; // (kept in case you patch other things elsewhere)
using NobleSociety.State;
using NobleSociety.Logging;
using System;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.CharacterDevelopment;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.CampaignSystem.Siege;
using NSLog = NobleSociety.Logging.FileLogger;
using NobleSociety.Patches;

namespace NobleSociety.Behaviors
{
    /// <summary>
    /// Handles game-side events that should produce social memories for nobles.
    /// Duplicates removed and weights tuned. Now also reacts to siege start.
    /// Bandit-hideout proximity patch removed per request.
    /// </summary>
    public class NobleMemoryEventsBehavior : CampaignBehaviorBase
    {
        #region Boilerplate
        public override void RegisterEvents()
        {
            CampaignEvents.HeroPrisonerReleased.AddNonSerializedListener(this, OnHeroReleased);
            CampaignEvents.HeroPrisonerTaken.AddNonSerializedListener(this, OnHeroTakenPrisoner);
            CampaignEvents.MapEventEnded.AddNonSerializedListener(this, OnMapEventEnded);
            CampaignEvents.OnPlayerBattleEndEvent.AddNonSerializedListener(this, OnPlayerBattleEnd);
            CampaignEvents.TournamentFinished.AddNonSerializedListener(this, OnTournamentFinished);
            CampaignEvents.OnSettlementOwnerChangedEvent.AddNonSerializedListener(this, OnSettlementOwnerChanged);

            // NEW: siege start hook
            // If your Bannerlord version uses a different signature/name, adjust the event and handler accordingly.
            CampaignEvents.OnSiegeEventStartedEvent.AddNonSerializedListener(this, OnSiegeStarted);
        }

        public override void SyncData(IDataStore dataStore) { }
        #endregion

        #region Siege start → memory + immediate relation impact
        private void OnSiegeStarted(SiegeEvent siege)
        {
            try
            {
                if (siege == null) return;
                var settlement = siege?.BesiegedSettlement;
                var besiegerHero = siege?.BesiegerCamp?.LeaderParty?.LeaderHero;
                var ownerHero = settlement?.OwnerClan?.Leader;

                if (settlement == null || besiegerHero == null || ownerHero == null) return;
                if (!besiegerHero.IsLord || !ownerHero.IsLord) return;
                if (besiegerHero == ownerHero) return;

                // Register a new memory so gossip can propagate later
                // Ensure you define MemoryType.SiegeStarted in your enum.
                NobleSocietyManager.RegisterMemory(
                    ownerHero,
                    besiegerHero,
                    MemoryType.SiegeStarted,
                    1.0f,
                    $"Siege begun against {settlement.Name} by {besiegerHero.Name}");

                NobleSocietyManager.RegisterMemory(
                    besiegerHero,
                    ownerHero,
                    MemoryType.SiegeStarted,
                    0.8f, // slightly lower weight for aggressor side
                    $"Laid siege to {settlement.Name}, owned by {ownerHero.Name}");

                // Immediate diplomatic fallout both ways (symmetric)
                ChangeRelationAction.ApplyRelationChangeBetweenHeroes(ownerHero, besiegerHero, -30);

                NSLog.Log($"[SIEGE-START] {besiegerHero.Name} began siege of {settlement.Name} (owner {ownerHero.Name}). Relations -30 both ways.");
            }
            catch (Exception ex)
            {
                NSLog.Log($"[ERROR] OnSiegeStarted threw: {ex}");
            }
        }
        #endregion

        #region Settlement ownership & politics
        private void OnSettlementOwnerChanged(Settlement settlement, bool openToClaim,
                                              Hero newOwner, Hero oldOwner, Hero capturerHero,
                                              ChangeOwnerOfSettlementAction.ChangeOwnerOfSettlementDetail detail)
        {
            if (settlement == null || !settlement.IsTown || oldOwner == null || newOwner == null)
                return;
            if (!oldOwner.IsLord || oldOwner.IsDead)
                return;

            if (detail == ChangeOwnerOfSettlementAction.ChangeOwnerOfSettlementDetail.BySiege)
            {
                NobleSocietyManager.RegisterMemory(
                    oldOwner,
                    capturerHero ?? newOwner,
                    MemoryType.LostSettlement,
                    1.0f,
                    $"Lost {settlement.Name} to {(capturerHero ?? newOwner).Name}")
                ;
                NSLog.Log($"[MEMORY] {oldOwner.Name} lost settlement {settlement.Name}");
            }
        }
        #endregion

        #region Prison / captivity (duplicates removed)
        private void OnHeroReleased(Hero releasedHero, PartyBase captorParty,
                                    IFaction captorFaction, EndCaptivityDetail detail)
        {
            if (releasedHero == null || !releasedHero.IsLord) return;
            Hero captor = captorParty?.LeaderHero;
            if (captor == null || !captor.IsLord) return;

            float weight = releasedHero.GetTraitLevel(DefaultTraits.Valor) >= 1 ? 1.0f : 0.8f;
            NobleSocietyManager.RegisterMemory(
                releasedHero, captor,
                MemoryType.ReleasedAfterBattle,
                weight,
                $"Released after battle by {captor.Name}");
            NSLog.Log($"[MEMORY] {releasedHero.Name} released by {captor.Name}");
        }

        private void OnHeroTakenPrisoner(PartyBase captorParty, Hero capturedHero)
        {
            if (capturedHero == null || !capturedHero.IsLord) return;
            Hero captor = captorParty?.LeaderHero;
            if (captor == null || !captor.IsLord) return;

            float weight = capturedHero.GetTraitLevel(DefaultTraits.Valor) >= 1 ? 1.2f : 1.0f;
            NobleSocietyManager.RegisterMemory(
                capturedHero, captor,
                MemoryType.Imprisoned,
                weight,
                $"Taken prisoner by {captor.Name}");
            NSLog.Log($"[MEMORY] {capturedHero.Name} imprisoned by {captor.Name}");
        }
        #endregion

        #region Battles
        private void OnPlayerBattleEnd(MapEvent mapEvent) => OnMapEventEnded(mapEvent);

        private void OnMapEventEnded(MapEvent mapEvent)
        {
            try
            {
                if (!mapEvent.IsFieldBattle)
                    return;

                var winnerEnum = mapEvent.WinningSide;
                if (winnerEnum == BattleSideEnum.None)
                    return;

                var winnerSide = mapEvent.GetMapEventSide(winnerEnum);
                var loserSide = mapEvent.GetMapEventSide(winnerEnum == BattleSideEnum.Attacker
                                                          ? BattleSideEnum.Defender
                                                          : BattleSideEnum.Attacker);

                var winnerLords = winnerSide.Parties
                                             .Select(p => p.Party?.LeaderHero ?? p.Party?.Owner)
                                             .Where(h => h != null && h.IsLord)
                                             .Distinct()
                                             .ToList();

                var loserLords = loserSide.Parties
                                             .Select(p => p.Party?.LeaderHero ?? p.Party?.Owner)
                                             .Where(h => h != null && h.IsLord)
                                             .Distinct()
                                             .ToList();

                if (winnerLords.Count == 0 || loserLords.Count == 0)
                    return;

                foreach (var winner in winnerLords)
                    foreach (var loser in loserLords)
                        NobleSocietyManager.RegisterMemory(
                            winner, loser,
                            MemoryType.BattleVictory,
                            1.0f,
                            $"Defeated {loser.Name} in battle");

                foreach (var loser in loserLords)
                    foreach (var winner in winnerLords)
                        NobleSocietyManager.RegisterMemory(
                            loser, winner,
                            MemoryType.BattleDefeat,
                            0.8f,
                            $"Defeated by {winner.Name} in battle");
            }
            catch (Exception ex)
            {
                NSLog.Log($"[ERROR] OnMapEventEnded threw: {ex}");
            }
            finally
            {
                // Clear per-mission knockout dedupe after each map event
                // (method provided by the new OnAgentRemovedPatch we’ll add next)
                OnAgentRemovedPatch.ClearDedupe();          // <-- ADD
            }
        }
        #endregion

        #region Tournaments
        private void OnTournamentFinished(CharacterObject winner,
                                          MBReadOnlyList<CharacterObject> participants,
                                          Town town, ItemObject prize)
        {
            try
            {
                var winningHero = winner?.HeroObject;
                if (winningHero == null || !winningHero.IsLord)
                    return;

                // Existing memory entry for the winner
                float weight = 0.8f + winningHero.GetTraitLevel(DefaultTraits.Valor) * 0.25f;
                NobleSocietyManager.RegisterMemory(
                    winningHero, winningHero,
                    MemoryType.TournamentVictory,
                    weight,
                    $"Won a tournament at {town.Name}");

                // --- NEW: Apply NPC-vs-NPC relation changes for off-screen tournaments ---
                // If the player participated, the mission patch already applied per-bout changes;
                // skip here to avoid double-applying.
                bool playerParticipated = false;
                if (participants != null)
                {
                    for (int i = 0; i < participants.Count; i++)
                    {
                        var co = participants[i];
                        var h = co?.HeroObject;
                        if (h == Hero.MainHero)
                        {
                            playerParticipated = true;
                            break;
                        }
                    }
                }
                if (playerParticipated)
                    return;

                // Constants match OnAgentRemovedPatch
                const int POS_GAIN = 2;   // battleDefeatedByPositivePlayerRelationGain
                const int NEG_LOSS = 2;   // battleDefeatedByNegativePlayerRelationLost

                if (participants != null)
                {
                    for (int i = 0; i < participants.Count; i++)
                    {
                        var co = participants[i];
                        var otherHero = co?.HeroObject;
                        if (otherHero == null || otherHero == winningHero)
                            continue;

                        // Only nobles (lords)
                        if (!otherHero.IsLord)
                            continue;

                        // Apply the same "positivity" rule as the mission patch:
                        // sum(Honor + Valor + Generosity + Mercy) >= 0 → positive
                        int traitSum =
                            otherHero.GetTraitLevel(DefaultTraits.Honor) +
                            otherHero.GetTraitLevel(DefaultTraits.Valor) +
                            otherHero.GetTraitLevel(DefaultTraits.Generosity) +
                            otherHero.GetTraitLevel(DefaultTraits.Mercy);

                        int delta = (traitSum >= 0) ? POS_GAIN : -NEG_LOSS;

                        if (delta != 0)
                        {
                            // No quick popup (keep UI quiet for NPC-vs-NPC)
                            ChangeRelationAction.ApplyRelationChangeBetweenHeroes(winningHero, otherHero, delta, false);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                NSLog.Log($"[ERROR] OnTournamentFinished relation updates threw: {ex}");
            }
            finally
            {
                // Clear per-mission/tournament dedupe containers used by OnAgentRemovedPatch
                OnAgentRemovedPatch.ClearDedupe();
            }
        }
        #endregion
    }
}
