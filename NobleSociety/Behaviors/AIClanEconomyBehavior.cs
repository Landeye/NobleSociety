using System;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Party.PartyComponents;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library; // MBRandom, CampaignTime

namespace NobleSociety.Behaviors
{
    /// <summary>
    /// Improves AI clan economic behavior: caravans, mercenary contracts, raiding, and expense management.
    /// </summary>
    public class AIClanEconomyBehavior : CampaignBehaviorBase
    {
        private const int CaravanCostThreshold = 15000;
        private const int MinimumClanGold = 5000;
        private const int DaysBetweenChecks = 7;

        private int _lastCheckDay = -1;

        public override void RegisterEvents()
        {
            CampaignEvents.DailyTickEvent.AddNonSerializedListener(this, DailyTick);
        }

        public override void SyncData(IDataStore dataStore) { }

        private void DailyTick()
        {
            int currentDay = (int)CampaignTime.Now.ToDays;
            if (_lastCheckDay >= 0 && currentDay - _lastCheckDay < DaysBetweenChecks)
                return;

            _lastCheckDay = currentDay;

            foreach (var clan in Campaign.Current.Clans)
            {
                if (clan == null || clan == Clan.PlayerClan || clan.IsEliminated || clan.IsMinorFaction || clan.Leader == null)
                    continue;

                HandleCaravanCreation(clan);
                HandleMercenaryContract(clan);
                EncourageRaiding(clan);
                ManageExpenses(clan);
            }
        }

        /// <summary>
        /// Creates a caravan for the clan if eligible, using CaravanPartyComponent.CreateCaravanParty.
        /// </summary>
        private void HandleCaravanCreation(Clan clan)
        {
            if (clan.Gold < CaravanCostThreshold)
                return;

            if (clan.Companions == null || clan.Companions.Count == 0)
                return;

            int activeCaravans = clan.Heroes.Count(h =>
                h != null &&
                h.PartyBelongedTo != null &&
                h.PartyBelongedTo.IsActive &&
                h.PartyBelongedTo.IsCaravan);

            int maxCaravans = Math.Max(1, Campaign.Current.Models.ClanTierModel.GetCompanionLimit(clan) / 2);
            if (activeCaravans >= maxCaravans)
                return;

            var companion = clan.Companions.FirstOrDefault(h =>
                h != null &&
                h.PartyBelongedTo == null &&
                !h.IsDisabled &&
                !h.IsDead &&
                !h.IsPrisoner &&
                h.GovernorOf == null);

            if (companion == null)
                return;

            Settlement origin =
                (companion.CurrentSettlement != null && companion.CurrentSettlement.IsTown) ? companion.CurrentSettlement :
                (clan.HomeSettlement != null && clan.HomeSettlement.IsTown) ? clan.HomeSettlement :
                Settlement.All.FirstOrDefault(s => s.IsTown);

            if (origin == null || !origin.IsTown || origin.SiegeEvent != null)
                return;

            // Spawn the caravan using the game API.
            CaravanPartyComponent.CreateCaravanParty(
                caravanOwner: clan.Leader,
                spawnSettlement: origin,
                isInitialSpawn: false,
                caravanLeader: companion,
                caravanItems: null,
                troopToBeGiven: 30,
                isElite: false
            );
        }

        /// <summary>
        /// Landless clans try to join a mercenary contract.
        /// Uses ChangeKingdomAction.ApplyByJoinFactionAsMercenary with int award factor.
        /// </summary>
        private void HandleMercenaryContract(Clan clan)
        {
            if (clan.Fiefs.Count > 0)
                return;

            if (clan.IsUnderMercenaryService)
                return;

            var targetKingdom = Kingdom.All
                .Where(k =>
                    k != null &&
                    !k.IsEliminated &&
                    !k.IsMinorFaction &&
                    !k.Clans.Contains(clan) &&
                    !FactionManager.IsAtWarAgainstFaction(clan, k))
                .OrderBy(_ => MBRandom.RandomInt())
                .FirstOrDefault();

            if (targetKingdom != null)
            {
                // Ensure int, as required by ApplyByJoinFactionAsMercenary.
                var awardFactor = (int)Campaign.Current.Models.MinorFactionsModel
                    .GetMercenaryAwardFactorToJoinKingdom(clan, targetKingdom);

                ChangeKingdomAction.ApplyByJoinFactionAsMercenary(clan, targetKingdom, awardFactor);
            }
        }

        /// <summary>
        /// Nudge AI when broke so they reconsider aggressive options (raids, etc.).
        /// Avoids calling internal-only methods.
        /// </summary>
        private void EncourageRaiding(Clan clan)
        {
            if (clan.Gold > MinimumClanGold)
                return;

            foreach (var partyComp in clan.WarPartyComponents)
            {
                var party = partyComp?.MobileParty;
                if (party == null || !party.IsActive || party.IsMainParty)
                    continue;

                // Allow fresh decision making and request a rethink.
                party.Ai.SetDoNotMakeNewDecisions(false);
                party.Ai.RethinkAtNextHourlyTick = true;
                party.Ai.RecalculateShortTermAi();

                // Optional: brief initiative boost to promote action for a few hours.
                party.Ai.SetInitiative(attackInitiative: 1.1f, avoidInitiative: 0.9f, hoursUntilReset: 6f);

                // Ensure they’re not stuck on a long pathing target.
                party.Ai.SetMoveModeHold();
            }
        }

        /// <summary>
        /// Disbands extra parties to reduce expenses if broke.
        /// </summary>
        private void ManageExpenses(Clan clan)
        {
            if (clan.Gold > 0)
                return;

            foreach (var partyComp in clan.WarPartyComponents)
            {
                var party = partyComp?.MobileParty;
                if (party == null || !party.IsActive || party.IsMainParty)
                    continue;

                // Keep the leader’s own party.
                if (party.LeaderHero == clan.Leader)
                    continue;

                // Don’t disrupt active encounters or sieges.
                if (party.MapEvent != null || party.SiegeEvent != null)
                    continue;

                DisbandPartyAction.StartDisband(party);
            }
        }
    }
}
