using HarmonyLib;
using System;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.BarterSystem.Barterables;
using NSLog = NobleSociety.Logging.FileLogger;

namespace NobleSociety.Patches
{
    [HarmonyPatch(typeof(SetPrisonerFreeBarterable))]
    public static class Patch_SetPrisonerFreeBarterable_GetUnitValueForFaction
    {
        // ILSpy shows: int GetUnitValueForFaction(IFaction faction)
        [HarmonyPatch(nameof(SetPrisonerFreeBarterable.GetUnitValueForFaction))]
        [HarmonyPatch(new Type[] { typeof(IFaction) })]
        [HarmonyPostfix]
        private static void Postfix(SetPrisonerFreeBarterable __instance, IFaction __0, ref int __result)
        {
            try
            {
                if (PrisonerBarterCtx.Map.TryGetValue(__instance, out var ctx) && ctx?.Prisoner != null)
                {
                    var dyn = NobleSociety.Systems.DynamicRansomLogic
                        .CalculateDynamicRansom(ctx.Prisoner, ctx.CaptorLeader, debug: false);

                    // If you want the *final* UI offer to match dyn exactly (game later multiplies by ~1.1x),
                    // you could pre-divide; otherwise just set to dyn:
                    __result = dyn;

                    // Optional trace:
                    NSLog.Log($"[DynamicRansom] Override -> {__result} for {ctx.Prisoner?.Name} (captor={ctx.CaptorLeader?.Name})");
                }
            }
            catch (Exception ex)
            {
                NSLog.Log("[DynamicRansom] GetUnitValueForFaction patch error: " + ex);
            }
        }
    }
}
