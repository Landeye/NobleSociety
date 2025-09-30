// NobleSociety/Systems/Relations/RelationRippleService.cs
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.CharacterDevelopment;

namespace NobleSociety.Systems.Relations
{
    internal static class RelationRippleService
    {
        // Tunables (can move to a config later)
        private const float ImmediateFamilyMult = 0.60f;
        private const float ClanmateMult = 0.35f;
        private const float CloseFriendMult = 0.30f;
        private const float LiegeVassalMult = 0.25f;
        private const int CapPerObserver = 5;

        // IMPORTANT: apply threshold to the FLOAT pre-round value
        private const double MinAbsThreshold = 0.5;

        // NEW: keep ripples focused and avoid kingdom-wide fan-out
        private const int MaxObserversPerEvent = 20;

        // NEW: only count genuinely close friends
        private const int FriendThreshold = 50;

        public static void ApplyRipples(
            Hero a,
            Hero b,
            int baseDelta,
            ChangeRelationAction.ChangeRelationDetail detail,
            bool showSummaryForPlayer)
        {
            var observers = new Dictionary<Hero, float>(64);

            Accumulate(observers, ImmediateFamilyOf(a), ImmediateFamilyMult);
            Accumulate(observers, ImmediateFamilyOf(b), ImmediateFamilyMult);

            Accumulate(observers, ClanmatesOf(a), ClanmateMult, exclude: b);
            Accumulate(observers, ClanmatesOf(b), ClanmateMult, exclude: a);

            Accumulate(observers, CloseFriendsOf(a), CloseFriendMult, exclude: b);
            Accumulate(observers, CloseFriendsOf(b), CloseFriendMult, exclude: a);

            Accumulate(observers, LiegeVassalCircleOf(a), LiegeVassalMult);
            Accumulate(observers, LiegeVassalCircleOf(b), LiegeVassalMult);

            observers.Remove(a);
            observers.Remove(b);

            if (observers.Count == 0) return;

            // NEW: limit to the most relevant observers (by ring weight, then closeness)
            var limited = observers
                .OrderByDescending(kv => kv.Value)
                .ThenByDescending(kv => Math.Max(a.GetRelation(kv.Key), b.GetRelation(kv.Key)))
                .Take(MaxObserversPerEvent)
                .ToArray();

            var target = RippleTarget(detail, a, b, baseDelta);
            var ctx = ContextMultiplier(detail, baseDelta);

            foreach (var kv in limited)
            {
                var observer = kv.Key;
                if (observer == null || !observer.IsAlive || observer == target) continue;

                var weight = kv.Value * ctx;
                var traitMod = TraitModifier(observer, detail, baseDelta);

                // NEW: symmetric attenuation near BOTH ends (±100) to prevent saturation
                float current = observer.GetRelation(target);      // -100..+100
                float edge = Math.Abs(current);
                float atten = 1f - Clamp((edge - 20f) / 80f, 0f, 0.9f);

                var rippleF = baseDelta * weight * traitMod * atten;

                // NEW: threshold on the FLOAT (pre-round), not the int
                if (Math.Abs(rippleF) < MinAbsThreshold) continue;

                var ripple = (int)Math.Round(rippleF, MidpointRounding.AwayFromZero);
                if (ripple > 0) ripple = Math.Min(ripple, CapPerObserver);
                else ripple = -Math.Min(-ripple, CapPerObserver);

                ChangeRelationAction.ApplyRelationChangeBetweenHeroes(
                    observer, target, ripple, showQuickNotification: false);
            }
        }

        private static void Accumulate(Dictionary<Hero, float> map, IEnumerable<Hero> set, float mult, Hero exclude = null)
        {
            if (set == null) return;
            foreach (var h in set)
            {
                if (h == null || !h.IsAlive || h == exclude) continue;
                if (map.TryGetValue(h, out var v)) { if (mult > v) map[h] = mult; }
                else map[h] = mult;
            }
        }

        // --- Social graph ---
        private static IEnumerable<Hero> ImmediateFamilyOf(Hero h)
        {
            if (h.Spouse != null) yield return h.Spouse;
            if (h.Father != null) yield return h.Father;
            if (h.Mother != null) yield return h.Mother;
            if (h.Children != null) foreach (var c in h.Children) yield return c;

            if (h.Father?.Children != null)
                foreach (var s in h.Father.Children) if (s != h) yield return s;
            if (h.Mother?.Children != null)
                foreach (var s in h.Mother.Children) if (s != h) yield return s;
        }

        private static IEnumerable<Hero> ClanmatesOf(Hero h)
        {
            var clan = h.Clan;
            if (clan?.Lords == null) yield break;
            foreach (var lord in clan.Lords) if (lord != h) yield return lord;
        }

        private static IEnumerable<Hero> CloseFriendsOf(Hero h)
        {
            foreach (var other in Hero.AllAliveHeroes)
                if (other != h && h.GetRelation(other) >= FriendThreshold) yield return other;
        }

        private static IEnumerable<Hero> LiegeVassalCircleOf(Hero h)
        {
            var clan = h.Clan;
            var kingdom = clan?.Kingdom;
            if (kingdom == null) yield break;

            if (kingdom.Leader != null && kingdom.Leader != h) yield return kingdom.Leader;
            foreach (var c in kingdom.Clans)
                foreach (var lord in c.Lords)
                    if (lord != h) yield return lord;
        }

        // --- Context / traits / target ---
        private static float ContextMultiplier(ChangeRelationAction.ChangeRelationDetail d, int baseDelta)
        {
            float mag = Math.Min(Math.Abs(baseDelta), 15); // cap contribution
            float mult = 0.30f + 0.02f * mag;               // 0.30 .. ~0.60
            if (d == ChangeRelationAction.ChangeRelationDetail.Emissary)
                mult *= 1.15f;                              // political bump
            return mult;
        }

        // SYMMETRIC: negatives are not reduced by design anymore
        private static float TraitModifier(Hero observer, ChangeRelationAction.ChangeRelationDetail d, int baseDelta)
        {
            int mercy = observer.GetTraitLevel(DefaultTraits.Mercy);
            int honor = observer.GetTraitLevel(DefaultTraits.Honor);
            int calculating = observer.GetTraitLevel(DefaultTraits.Calculating);

            float magnitudeBoost = 1f + 0.03f * (Math.Abs(mercy) + Math.Abs(honor));
            float politicalBoost = (d == ChangeRelationAction.ChangeRelationDetail.Emissary)
                ? (1f + 0.03f * Math.Abs(calculating))
                : 1f;

            return magnitudeBoost * politicalBoost;
        }

        private static Hero RippleTarget(ChangeRelationAction.ChangeRelationDetail d, Hero a, Hero b, int baseDelta)
            => baseDelta >= 0 ? b : a;

        // small helper
        private static float Clamp(float v, float min, float max)
            => v < min ? min : (v > max ? max : v);
    }
}
