// NobleSociety/Patches/TournamentPreferLocalNoblesPatch.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.CampaignSystem.TournamentGames;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace NobleSociety.Patches
{
    /// <summary>
    /// Prefer local nobles (lords currently in the settlement) by reordering
    /// the existing vanilla participant list. We DO NOT inject new participants,
    /// only move eligible locals to the front to avoid bracket crashes.
    /// </summary>
    [HarmonyPatch]
    internal static class TournamentPreferLocalNoblesPatch
    {
        // Target all overrides of: MBList<CharacterObject> GetParticipantCharacters(Settlement, bool)
        [HarmonyTargetMethods]
        private static IEnumerable<MethodBase> TargetMethods()
        {
            var baseType = typeof(TournamentGame);
            var wanted = new[] { typeof(Settlement), typeof(bool) };

            foreach (var t in AccessTools.AllTypes().Where(tt => tt.IsClass && !tt.IsAbstract && baseType.IsAssignableFrom(tt)))
            {
                var m = AccessTools.DeclaredMethod(t, "GetParticipantCharacters", wanted);
                if (m != null && !m.IsAbstract)
                    yield return m;
            }
        }

        [HarmonyPostfix]
        private static void Postfix(
            ref MBList<CharacterObject> __result,
            Settlement settlement,
            bool includePlayer)
        {
            // Safety
            if (settlement == null || __result == null || __result.Count == 0)
                return;

            // Copy vanilla participants and index by StringId
            var vanilla = __result.ToList();
            var vanillaIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in vanilla)
            {
                var id = c?.StringId;
                if (!string.IsNullOrEmpty(id))
                    vanillaIds.Add(id);
            }

            // Find local nobles already present in the vanilla list
            var localEligibleIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var h in settlement.HeroesWithoutParty)
            {
                if (h == null || !h.IsLord || !h.IsAlive || h.IsPrisoner || h.Age < 18f)
                    continue;
                if (!includePlayer && h.CharacterObject != null && h.CharacterObject.IsPlayerCharacter)
                    continue;

                var id = h.CharacterObject?.StringId;
                if (!string.IsNullOrEmpty(id) && vanillaIds.Contains(id))
                    localEligibleIds.Add(id);
            }

            if (localEligibleIds.Count == 0)
                return; // nothing to reorder

            // Reorder: locals (in original order) first, then the rest (also original order)
            var reordered = new List<CharacterObject>(vanilla.Count);

            foreach (var c in vanilla)
            {
                var id = c?.StringId;
                if (!string.IsNullOrEmpty(id) && localEligibleIds.Contains(id))
                    reordered.Add(c);
            }
            foreach (var c in vanilla)
            {
                var id = c?.StringId;
                if (string.IsNullOrEmpty(id) || localEligibleIds.Contains(id))
                    continue;
                reordered.Add(c);
            }

            __result = new MBList<CharacterObject>(reordered);
        }
    }
}
