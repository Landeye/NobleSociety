using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.Library;
using NobleSociety.Systems;
using TaleWorlds.Core;
using NobleSociety.Logging; // for FileLogger

namespace NobleSociety.Behaviors
{
    public class NoblePatronageBehavior : CampaignBehaviorBase
    {
        private readonly Dictionary<(string donorId, string recipientId), PatronageLogic.GiftTracker> _recentGifts =
            new Dictionary<(string donorId, string recipientId), PatronageLogic.GiftTracker>();

        public override void RegisterEvents()
        {
            CampaignEvents.DailyTickClanEvent.AddNonSerializedListener(this, OnDailyTickClan);
            if (PatronageLogic.DebugPatronage)
                FileLogger.Log("[Patronage] NoblePatronageBehavior registered DailyTickClanEvent.");
        }

        public override void SyncData(IDataStore dataStore)
        {
            List<string> keys = new List<string>(), values = new List<string>();
            if (dataStore.IsSaving)
            {
                foreach (var kv in _recentGifts)
                {
                    var k = kv.Key; var g = kv.Value;
                    keys.Add($"{k.donorId}|{k.recipientId}");
                    values.Add(string.Join(";",
                        g.WindowStartDay.ToString(CultureInfo.InvariantCulture),
                        g.RelationGainedThisWindow.ToString(CultureInfo.InvariantCulture),
                        g.LastGiftDay.ToString(CultureInfo.InvariantCulture),
                        g.GiftsGivenThisWindow.ToString(CultureInfo.InvariantCulture),
                        g.GiftsReceivedThisWindow.ToString(CultureInfo.InvariantCulture)
                    ));
                }
            }
            dataStore.SyncData("patronageGiftKeys", ref keys);
            dataStore.SyncData("patronageGiftVals", ref values);
            if (dataStore.IsLoading && keys != null && values != null)
            {
                _recentGifts.Clear();
                for (int i = 0; i < Math.Min(keys.Count, values.Count); i++)
                {
                    var keyParts = keys[i].Split('|');
                    if (keyParts.Length != 2) continue;
                    var valParts = values[i].Split(';');
                    if (valParts.Length != 5) continue;

                    _recentGifts[(keyParts[0], keyParts[1])] = new PatronageLogic.GiftTracker
                    {
                        WindowStartDay = float.Parse(valParts[0], CultureInfo.InvariantCulture),
                        RelationGainedThisWindow = int.Parse(valParts[1], CultureInfo.InvariantCulture),
                        LastGiftDay = float.Parse(valParts[2], CultureInfo.InvariantCulture),
                        GiftsGivenThisWindow = int.Parse(valParts[3], CultureInfo.InvariantCulture),
                        GiftsReceivedThisWindow = int.Parse(valParts[4], CultureInfo.InvariantCulture)
                    };
                }
                if (PatronageLogic.DebugPatronage)
                    FileLogger.Log($"[Patronage] Loaded {_recentGifts.Count} donor-recipient windows from save.");
            }
        }

        private static bool IsEligibleDonor(Hero h, Clan expectedClan)
        {
            return h != null
                && h.IsAlive
                && !h.IsChild
                && !h.IsPrisoner
                && !h.IsDead
                && h.Clan == expectedClan;
        }

        private void OnDailyTickClan(Clan clan)
        {
            // Leaders only to reduce CPU & spam
            var donor = clan.Leader;
            if (!IsEligibleDonor(donor, clan))
                return;

            float now = (float)CampaignTime.Now.ToDays;

            // Prefer poorer recipients first (leaders only), then a light shuffle
            var candidates = PatronageLogic.GetEligibleRecipients(donor, false)
                .Where(r => !r.IsPrisoner && !r.IsDead && r == r.Clan?.Leader) // recipients must be leaders
                .OrderBy(r => PatronageLogic.GetClanSurplus(r.Clan))           // neediest first (most negative surplus)
                .ThenBy(r => r.Clan?.Gold ?? int.MaxValue)
                .ThenBy(_ => MBRandom.RandomFloat)
                .Take(5)
                .ToList();

            // (optional) sample log to avoid spam
            if (PatronageLogic.DebugPatronage && candidates.Count > 0 && MBRandom.RandomFloat < 0.02f)
                FileLogger.Log($"[Patronage] Donor={donor.Name} candidates≈{candidates.Count}");

            foreach (var recipient in candidates)
            {
                var key = (donor.StringId, recipient.StringId);
                if (!_recentGifts.TryGetValue(key, out var window))
                    window = _recentGifts[key] = new PatronageLogic.GiftTracker { WindowStartDay = now };

                // Decay: apply for each block since last gift
                if (window.LastGiftDay > 0 && now - window.LastGiftDay >= PatronageLogic.GiftDecayBlockDays)
                {
                    int blocks = (int)((now - window.LastGiftDay) / PatronageLogic.GiftDecayBlockDays);
                    int penalty = blocks * PatronageLogic.GiftSoftDecayPerBlock;
                    if (penalty > 0)
                    {
                        ChangeRelationAction.ApplyRelationChangeBetweenHeroes(donor, recipient, -penalty);
                        window.RelationGainedThisWindow = Math.Max(0, window.RelationGainedThisWindow - penalty);
                        window.LastGiftDay += blocks * PatronageLogic.GiftDecayBlockDays;

                        if (PatronageLogic.DebugPatronage)
                            FileLogger.Log($"[Patronage] Decay applied {donor.Name}→{recipient.Name}: -{penalty} (blocks={blocks}) newWindowGain={window.RelationGainedThisWindow}");
                    }
                }

                // Reset rolling window
                if (now - window.WindowStartDay > PatronageLogic.WindowDays)
                {
                    if (PatronageLogic.DebugPatronage)
                        FileLogger.Log($"[Patronage] Reset window {donor.Name}→{recipient.Name}: prev ΔR={window.RelationGainedThisWindow}, giftsSent={window.GiftsGivenThisWindow}, giftsRecv={window.GiftsReceivedThisWindow}");

                    window.RelationGainedThisWindow = 0;
                    window.GiftsGivenThisWindow = 0;
                    window.GiftsReceivedThisWindow = 0;
                    window.WindowStartDay = now;
                }

                // Global donor/recipient counts across all pairs for cap enforcement
                int giftsGivenInWindow = _recentGifts
                    .Where(kv => kv.Key.donorId == donor.StringId && (now - kv.Value.WindowStartDay) <= PatronageLogic.WindowDays)
                    .Sum(kv => kv.Value.GiftsGivenThisWindow);

                int recipientGiftsInWindow = _recentGifts
                    .Where(kv => kv.Key.recipientId == recipient.StringId && (now - kv.Value.WindowStartDay) <= PatronageLogic.WindowDays)
                    .Sum(kv => kv.Value.GiftsReceivedThisWindow);

                int currentRelation = donor.GetRelation(recipient);
                bool sameKingdom = donor.Clan?.Kingdom != null && donor.Clan.Kingdom == recipient.Clan?.Kingdom;

                // Smarter amount (capped by donor surplus & recipient need)
                int amount = PatronageLogic.DetermineGiftAmount(donor, recipient);

                // Safety downshift if donor buffer would be violated
                int attempts = 0;
                while (attempts < 2 && donor.Clan.Gold - amount < PatronageLogic.GiftFloor + 20 * PatronageLogic.GetClanTotalWage(donor.Clan))
                {
                    amount = Math.Max(PatronageLogic.GiftMin, amount / 2);
                    attempts++;
                }

                if (!PatronageLogic.ShouldDonorGift(donor, recipient, giftsGivenInWindow, recipientGiftsInWindow, currentRelation, amount))
                    continue;

                // (optional) wealth comparison line for clarity
                if (PatronageLogic.DebugPatronage)
                    FileLogger.Log($"[Patronage] Check {donor.Name}({donor.Clan?.Gold}) → {recipient.Name}({recipient.Clan?.Gold}) donorSurp={PatronageLogic.GetClanSurplus(donor.Clan)} recipSurp={PatronageLogic.GetClanSurplus(recipient.Clan)}");

                float traitMult = PatronageLogic.GetTraitMultiplier(donor, recipient, sameKingdom);
                int proposedDelta = PatronageLogic.CalculateRelationDelta(amount, traitMult);

                int remaining = PatronageLogic.MaxRelationPerWindow - window.RelationGainedThisWindow;
                int deltaToApply = Math.Max(0, Math.Min(proposedDelta, remaining));
                if (deltaToApply <= 0)
                    continue;

                if (PatronageLogic.DebugPatronage)
                    FileLogger.Log($"[Patronage] Applying gift: {donor.Name}→{recipient.Name} amt={amount} traitMult={traitMult:0.00} ΔR_proposed={proposedDelta} remaining={remaining} ΔR_applied={deltaToApply} caps(donor={giftsGivenInWindow}, recip={recipientGiftsInWindow})");

                // Apply gift (routes leader→leader internally)
                PatronageLogic.ApplyGift(donor, recipient, amount, deltaToApply, null);

                window.RelationGainedThisWindow += deltaToApply;
                window.GiftsGivenThisWindow++;
                window.GiftsReceivedThisWindow++;
                window.LastGiftDay = now;

                break; // one gift per day per clan
            }
        }
    }
}
