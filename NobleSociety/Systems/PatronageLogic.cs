using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.Library;
using NobleSociety.Logging; // for FileLogger
using TaleWorlds.Core;

namespace NobleSociety.Systems
{
    public static class PatronageLogic
    {
        public static bool DebugPatronage = true;

        // Config
        public const float WindowDays = 30f;
        public const int GiftFloor = 50000, GiftMin = 10000, GiftMax = 60000;
        public const int MaxGiftsPerDonorPerWindow = 2, MaxGiftsPerRecipientPerWindow = 1, MaxRelationPerWindow = 6, MaxRelation = 80;
        public const float GiftDecayBlockDays = 60f;
        public const int GiftSoftDecayPerBlock = 1;
        public const int EnemyRelationThreshold = -20; // For enemy-of-enemy logic

        public class GiftTracker
        {
            public float WindowStartDay;
            public int RelationGainedThisWindow;
            public float LastGiftDay;
            public int GiftsGivenThisWindow;
            public int GiftsReceivedThisWindow;
        }

        private static float Clamp(float v, float min, float max) => v < min ? min : (v > max ? max : v);

        // ===== Surplus / Need helpers =====
        public static int GetClanTotalWage(Clan clan)
        {
            if (clan == null) return 0;
            int sum = 0;
            foreach (var party in clan.WarPartyComponents)
            {
                var mobileParty = party.MobileParty;
                if (mobileParty != null)
                    sum += mobileParty.TotalWage;
            }
            return sum;
        }

        public static int GetClanSurplus(Clan clan)
        {
            if (clan == null) return int.MinValue;
            int wageBuffer = 20 * GetClanTotalWage(clan);
            return (clan.Gold) - (GiftFloor + wageBuffer);
        }

        public static bool RecipientNeedsAid(Clan recipientClan)
        {
            if (recipientClan == null) return false;
            return recipientClan.Gold < GiftFloor || GetClanSurplus(recipientClan) < 0;
        }

        // ===== Traits & relation =====
        public static float GetTraitMultiplier(Hero donor, Hero recipient, bool sameKingdom)
        {
            float m = 1.0f;
            var donorTraits = donor.GetHeroTraits();
            var recipientTraits = recipient.GetHeroTraits();

            if (donorTraits.Generosity > 0) m *= 1.25f;
            if (donorTraits.Calculating > 0) m *= 0.85f;
            if (donorTraits.Honor > 0 && !sameKingdom) m *= 0.9f;
            if (donorTraits.Mercy > 0 && recipient.Clan?.Gold < GiftFloor) m *= 1.1f;
            if (recipientTraits.Generosity > 0) m *= 1.1f;
            if (recipientTraits.Honor > 0 && sameKingdom) m *= 1.1f;
            if (recipientTraits.Honor > 0 && donorTraits.Calculating > 0) m *= 0.9f;
            if (recipientTraits.Calculating > 0) m *= 1.1f;
            if (recipientTraits.Mercy > 0 && donor.Clan?.Gold < GiftFloor) m *= 1.1f;

            return Clamp(m, 0.8f, 1.2f);
        }

        public static int CalculateRelationDelta(int giftAmount, float traitMultiplier)
        {
            double baseDelta = Math.Round(2 * Math.Sqrt(giftAmount / 20000.0));
            double result = baseDelta * traitMultiplier;
            return (int)Math.Min(result, MaxRelationPerWindow);
        }

        // ===== Gift decision =====
        public static bool ShouldDonorGift(Hero donor, Hero recipient, int giftsGivenInWindow, int recipientGiftsInWindow, int currentRelation, int amount)
        {
            if (donor == null || recipient == null) return false;
            if (donor.Clan == null || donor.Clan.Gold < GiftFloor) return false;
            if (recipient.Clan == null) return false;
            if (recipient.IsPrisoner || recipient.IsChild || recipient.IsDead) return false;
            if (giftsGivenInWindow >= MaxGiftsPerDonorPerWindow) return false;
            if (recipientGiftsInWindow >= MaxGiftsPerRecipientPerWindow) return false;
            if (currentRelation >= MaxRelation) return false;
            if (donor == recipient) return false;

            // Leader-only recipients
            if (recipient != recipient.Clan?.Leader) return false;

            // Relative-wealth guardrails
            var donorClan = donor.Clan;
            var recipClan = recipient.Clan;
            int donorGold = donorClan?.Gold ?? 0;
            int recipGold = recipClan?.Gold ?? 0;

            int donorSurplus = GetClanSurplus(donorClan);
            int recipSurplus = GetClanSurplus(recipClan);
            bool recipNeedsAid = RecipientNeedsAid(recipClan);

            // Donor must have surplus
            if (donorSurplus <= 0) return false;

            // If recipient not in need and roughly as rich or richer (>=90% of donor), skip
            if (!recipNeedsAid && recipGold >= (int)(0.9f * donorGold))
                return false;

            // Strong rule: never send upward if recipient is richer AND not in deficit
            if (recipGold > donorGold && recipSurplus >= 0)
                return false;

            // Donor buffer check
            int wageBuffer = 20 * GetClanTotalWage(donor.Clan);
            if (donor.Clan.Gold - amount < GiftFloor + wageBuffer) return false;
            if (donor.IsPrisoner || donor.IsChild || donor.IsDead) return false;

            return true;
        }

        // ===== Gift amount =====

        // Old API kept for compatibility
        public static int DetermineGiftAmount(Hero donor)
        {
            int generosity = donor.GetHeroTraits().Generosity;
            float generosityMult = generosity > 0 ? 1.25f : 1f;
            int min = (int)(GiftMin * generosityMult);
            int max = (int)(GiftMax * generosityMult);
            return (int)MBRandom.RandomInt(min, max + 1); // upper-exclusive
        }

        // New API: cap by donor surplus and recipient need
        public static int DetermineGiftAmount(Hero donor, Hero recipient)
        {
            int generosity = donor.GetHeroTraits().Generosity;
            float generosityMult = generosity > 0 ? 1.25f : 1f;

            int donorSurplus = Math.Max(0, GetClanSurplus(donor.Clan));
            int recipWageBuf = 20 * GetClanTotalWage(recipient.Clan);
            int recipTarget = Math.Max(GiftFloor, recipWageBuf + GiftFloor / 2); // cushion
            int recipGold = recipient.Clan?.Gold ?? 0;
            int recipNeed = Math.Max(0, recipTarget - recipGold);

            int min = (int)(GiftMin * generosityMult);
            int max = (int)(GiftMax * generosityMult);

            int hardCap = Math.Max(min, Math.Min(max, Math.Min(donorSurplus, recipNeed)));
            if (hardCap <= min) return min; // let ShouldDonorGift decide if small gifts make sense

            int roll = MBRandom.RandomInt(min, Math.Max(min + 1, hardCap + 1));
            return Math.Min(roll, hardCap);
        }

        // ===== Recipient pool =====
        /// <summary>
        /// Returns eligible recipients for gifting. If allowEnemyOfEnemy is true,
        /// includes nobles who are enemies of at least one of the donor's enemies (and have non-negative relation with donor).
        /// </summary>
        public static IEnumerable<Hero> GetEligibleRecipients(Hero donor, bool allowEnemyOfEnemy, Func<Hero, bool> customFilter = null)
        {
            var donorKingdom = donor.Clan?.Kingdom;

            var donorEnemies = Hero.AllAliveHeroes
                .Where(h => h != donor && h.IsLord && !h.IsPrisoner && !h.IsDead && donor.GetRelation(h) <= EnemyRelationThreshold)
                .ToList();

            var potential = Hero.AllAliveHeroes
                .Where(h =>
                    h.IsLord &&
                    h != donor &&
                    !h.IsPrisoner && !h.IsDead &&
                    h.Clan != null &&
                    h.Clan != donor.Clan &&
                    h.Clan.Kingdom != null &&
                    (
                        (donorKingdom != null && h.Clan.Kingdom == donorKingdom)
                        || (allowEnemyOfEnemy && donorEnemies.Any(enemy =>
                            !enemy.IsPrisoner && !enemy.IsDead &&
                            h.GetRelation(enemy) <= EnemyRelationThreshold &&
                            donor.GetRelation(h) >= 0))
                    )
                );

            if (customFilter != null)
                potential = potential.Where(customFilter);

            return potential;
        }

        // ===== Apply gift =====
        public static void ApplyGift(Hero donor, Hero recipient, int amount, int deltaRelation, Action<string> debugLog = null)
        {
            // Force leader → leader transfer to align with clan treasury expectations
            var giver = donor?.Clan?.Leader ?? donor;
            var receiver = recipient?.Clan?.Leader ?? recipient;
            if (giver == null || receiver == null) return;

            GiveGoldAction.ApplyBetweenCharacters(giver, receiver, amount, disableNotification: true);
            ChangeRelationAction.ApplyRelationChangeBetweenHeroes(giver, receiver, deltaRelation);

            if (DebugPatronage)
            {
                FileLogger.Log($"[Patronage] Gift {giver?.Name} → {receiver?.Name}: {amount}g, ΔR={deltaRelation} (donorClanGold={giver?.Clan?.Gold}, recipClanGold={receiver?.Clan?.Gold})");
            }
        }
    }
}
