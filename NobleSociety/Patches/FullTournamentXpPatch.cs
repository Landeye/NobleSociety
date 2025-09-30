// NobleSociety/Compat/FullTournamentXpPatch.cs
using System; // <-- add this
using HarmonyLib;
using TaleWorlds.CampaignSystem.ComponentInterfaces;
using TaleWorlds.CampaignSystem.GameComponents;

namespace NobleSociety.Compat
{
    [HarmonyPatch(typeof(DefaultCombatXpModel), nameof(DefaultCombatXpModel.GetXpFromHit))]
    internal static class FullTournamentXpPatch
    {
        static void Postfix(CombatXpModel.MissionTypeEnum missionType, ref int xpAmount)
        {
            if (missionType == CombatXpModel.MissionTypeEnum.Tournament)
            {
                const double ScaleToFull = 100.0 / 33.0; // undo vanilla ~33%
                xpAmount = (int)Math.Round(xpAmount * ScaleToFull);
            }
        }
    }
}

