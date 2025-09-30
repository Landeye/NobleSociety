using System;
using System.Reflection;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.Library;
using NobleSociety.Logging; // For FileLogger

namespace NobleSociety.Systems
{
    public static class DynamicRansomLogic
    {
        // Cache for reflection fallback
        private static MethodInfo _ransomMethod;
        private static Type _cachedModelType;

        private static float Clamp(float v, float min, float max) => v < min ? min : (v > max ? max : v);
        private static float Lerp(float a, float b, float t) => a + (b - a) * t;

        private static MethodInfo ResolveRansomMethod(object model)
        {
            var t = model.GetType();
            const BindingFlags bf = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            return t.GetMethod("GetHeroRansomValue", bf, null, new[] { typeof(Hero) }, null)
                ?? t.GetMethod("GetRansomValue", bf, null, new[] { typeof(Hero) }, null);
        }

        // Wealth curve multipliers (now uses captive's clan)
        public static float CalculateWealthMultiplier(Clan captiveClan, float alpha = 0.35f, float cap = 2.25f)
        {
            float gold = Math.Max(captiveClan?.Gold ?? 0, 10000);
            float mult = 1.0f + alpha * (float)Math.Log10(gold / 10000f);
            return Clamp(mult, 1.0f, cap);
        }

        // Trait/context multipliers for the captor
        public static float GetTraitContextMultiplier(Hero captor, Hero captive)
        {
            float mult = 1.0f;
            if (captor == null || captive == null) return mult;

            var traits = captor.GetHeroTraits(); // Use your HeroTraitExtensions

            if (traits.Calculating > 0 || traits.Generosity < 0)
                mult *= 1.10f; // Greedy/Calculating

            if (traits.Honor > 0 || traits.Mercy > 0)
                mult *= 0.95f;

            // Use kingdom equality for same-kingdom discount (safer than MapFaction)
            if (captor.Clan?.Kingdom != null && captor.Clan.Kingdom == captive.Clan?.Kingdom)
                mult *= 0.90f;

            // Optional: Tiny relation flavor
            float rel = captor.GetRelation(captive);
            mult *= Lerp(0.98f, 1.02f, (rel / 100f + 1f) / 2f);

            return mult;
        }

        // Helper to sum all party wages for a clan (optionally add garrisons for stricter buffer)
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
            // Optionally add garrison wages here.
            return sum;
        }

        // Master ransom calculation
        public static int CalculateDynamicRansom(Hero captive, Hero captor, bool debug = false)
        {
            var ransomModel = Campaign.Current.Models.RansomValueCalculationModel;

            // Null/edge guards
            if (captive == null || captor == null)
                return 5000;

            // Robust method resolution in case another mod swaps the model
            if (_ransomMethod == null || _cachedModelType != ransomModel.GetType())
            {
                _ransomMethod = ResolveRansomMethod(ransomModel);
                _cachedModelType = ransomModel.GetType();
            }

            int baseRansom = 5000;
            try
            {
                if (_ransomMethod != null)
                    baseRansom = (int)_ransomMethod.Invoke(ransomModel, new object[] { captive });
            }
            catch
            {
                // fallback remains 5000
            }

            float wealthMult = CalculateWealthMultiplier(captive.Clan);
            float traitMult = GetTraitContextMultiplier(captor, captive);
            float ransom = baseRansom * wealthMult * traitMult;
            float min = 0.85f * baseRansom, max = baseRansom * 2.25f;
            ransom = Clamp(ransom, min, max);

            {
                //FileLogger.Log($"[DynamicRansom] base={baseRansom} wealthMult={wealthMult:0.00} traitMult={traitMult:0.00} ransom={ransom:0} (clamp {min:0}–{max:0})");
            }

            return (int)Math.Round(ransom);
        }

        // AI: Should captor accept?
        public static bool CaptorShouldAccept(int offer, int ask, Clan captorClan)
        {
            if (offer >= 0.85f * ask) return true;
            if (captorClan?.Gold < 30000) return true;
            return false;
        }

        // AI: Should captive pay?
        public static bool CaptiveShouldPay(Hero captive, int ransomAmount)
        {
            int wageBuffer = 20 * GetClanTotalWage(captive.Clan);
            return captive.Clan?.Gold > ransomAmount + wageBuffer;
        }

        // Installments: for poor captor (<30k), not poor captive
        public static (int upfront, int deferred) GetInstallmentPlan(Clan captorClan, int ransom)
        {
            if (captorClan?.Gold < 30000)
            {
                int upfront = ransom / 2;
                int deferred = ransom - upfront;
                return (upfront, deferred);
            }
            return (ransom, 0);
        }
    }
}
