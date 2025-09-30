// NobleSociety/Patches/TournamentNobleSeedingPatch.cs
using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.CharacterDevelopment;
using TaleWorlds.CampaignSystem.GameComponents;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.CampaignSystem.TournamentGames;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace NobleSociety.Patches
{
    /// <summary>
    /// When a tournament is created, quietly move a few eligible idle lords into the host town
    /// so vanilla will naturally pick more nobles as participants. Designed to be conservative
    /// and to cooperate with IdleNobleNeutralSeatingBehavior (short standalone lock).
    /// </summary>
    [HarmonyPatch(typeof(DefaultTournamentModel), "CreateTournament")]
    internal static class TournamentNobleSeedingPatch
    {
        // ---- Tunables (independent from IdleNobleNeutralSeatingBehavior, but similar philosophy)
        private const int DesiredNobleAdds = 4;    // try to add up to this many idle nobles
        private const int TownNobleCap = 10;   // maximum total lords we allow inside the town after seeding
        private const float MaxDistance = 140f; // max distance from town to pull an idle noble
        private const float LockDaysMin = 3f;   // keep them seated briefly so tournament can pick them
        private const float LockDaysMax = 5f;

        // Keep a short lock so we don't keep reseating the same lord repeatedly
        private static readonly Dictionary<string, float> _seedLockUntil = new Dictionary<string, float>(256);

        // Postfix signature: (Town town, ref TournamentGame __result)
        static void Postfix(Town town, ref TournamentGame __result)
        {
            try
            {
                if (town == null || __result == null) return;

                var settlement = town.Settlement;
                if (settlement == null || settlement.MapFaction == null) return;

                // Count lords currently inside town (without party)
                int currentLordsInTown = 0;
                foreach (var h in settlement.HeroesWithoutParty)
                    if (h != null && h.IsLord && h.IsAlive && !h.IsPrisoner) currentLordsInTown++;

                // Headroom to seed new lords
                int headroom = Math.Max(0, TownNobleCap - currentLordsInTown);
                if (headroom == 0) return;

                int toSeat = Math.Min(DesiredNobleAdds, headroom);
                if (toSeat <= 0) return;

                // Pull candidates: idle lords of same kingdom, adult, alive, not prisoners, not already in town, within range
                var kingdom = town.OwnerClan?.Kingdom;
                if (kingdom == null) return;

                var townPos = settlement.Position2D;
                float now = (float)CampaignTime.Now.ToDays;

                // Helpers to skip locked or recently seated by us
                bool IsSeedLocked(Hero h)
                {
                    float until;
                    return _seedLockUntil.TryGetValue(h.StringId, out until) && now < until;
                }

                // Gather and score candidates
                var candidates = new List<Hero>(64);
                foreach (var h in Hero.AllAliveHeroes)
                {
                    if (h == null || !h.IsLord || h.IsChild) continue;
                    if (h.IsPrisoner) continue;
                    if (h == Hero.MainHero) continue; // never touch player
                    if (h.Clan == null || h.Clan.Kingdom != kingdom) continue;

                    // Must be idle (no party) and not already in town
                    if (h.PartyBelongedTo != null) continue;
                    if (h.CurrentSettlement == settlement) continue;

                    // Skip if under our short seed lock
                    if (IsSeedLocked(h)) continue;

                    // Must have a settlement to measure distance from (either current or clan center)
                    var from = h.CurrentSettlement ?? h.Clan?.Fiefs?.FirstOrDefault()?.Settlement;
                    if (from == null) continue;

                    // Distance gate
                    float d = from.Position2D.Distance(townPos);
                    if (d > MaxDistance) continue;

                    candidates.Add(h);
                }

                if (candidates.Count == 0) return;

                // Rank by proximity (closest first), slight bias for valor
                candidates = candidates
                    .OrderBy(h =>
                    {
                        var from = h.CurrentSettlement ?? h.Clan?.Fiefs?.FirstOrDefault()?.Settlement;
                        return from == null ? float.MaxValue : from.Position2D.Distance(townPos);
                    })
                    .ThenByDescending(h => h.GetTraitLevel(DefaultTraits.Valor))
                    .Take(toSeat * 2) // take a small buffer, we may fail some moves
                    .ToList();

                int seated = 0;
                foreach (var h in candidates)
                {
                    if (seated >= toSeat) break;

                    // Capacity recheck (town might have filled by concurrent systems)
                    currentLordsInTown = 0;
                    foreach (var inTown in settlement.HeroesWithoutParty)
                        if (inTown != null && inTown.IsLord && inTown.IsAlive && !inTown.IsPrisoner) currentLordsInTown++;
                    if (currentLordsInTown >= TownNobleCap) break;

                    // Move hero: leave current settlement if needed, then enter the target town
                    try
                    {
                        if (h.CurrentSettlement != null)
                            LeaveSettlementAction.ApplyForCharacterOnly(h);

                        EnterSettlementAction.ApplyForCharacterOnly(h, settlement);

                        // Apply our short seed lock
                        float lockFor = MBRandom.RandomFloatRanged(LockDaysMin, LockDaysMax);
                        _seedLockUntil[h.StringId] = now + lockFor;

                        seated++;
                    }
                    catch
                    {
                        // Ignore failure for this hero and continue
                    }
                }
            }
            catch
            {
                // Swallow to avoid disrupting tournament creation
            }
        }
    }
}
