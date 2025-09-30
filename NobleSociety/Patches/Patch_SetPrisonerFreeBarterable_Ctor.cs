using HarmonyLib;
using System;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.BarterSystem.Barterables;
using TaleWorlds.CampaignSystem.Party;
using NSLog = NobleSociety.Logging.FileLogger;

namespace NobleSociety.Patches
{
    [HarmonyPatch(typeof(SetPrisonerFreeBarterable))]
    public static class Patch_SetPrisonerFreeBarterable_Ctor
    {
        // ctor: (Hero prisoner, Hero captorLeader, PartyBase party, Hero ransomPayer)
        [HarmonyPatch(MethodType.Constructor)]
        [HarmonyPatch(new Type[] { typeof(Hero), typeof(Hero), typeof(PartyBase), typeof(Hero) })]
        [HarmonyPostfix]
        private static void Postfix(SetPrisonerFreeBarterable __instance, Hero __0, Hero __1)
        {
            try
            {
                PrisonerBarterCtx.Map.Remove(__instance);
                PrisonerBarterCtx.Map.Add(__instance, new PrisonerBarterCtx.Ctx
                {
                    Prisoner = __0,
                    CaptorLeader = __1
                });
                // Optional trace:
                // NSLog.Log($"[DynamicRansom] Captured ctx: prisoner={__0?.Name}, captor={__1?.Name}");
            }
            catch (Exception ex)
            {
                NSLog.Log("[DynamicRansom] Ctor patch error: " + ex);
            }
        }
    }
}

