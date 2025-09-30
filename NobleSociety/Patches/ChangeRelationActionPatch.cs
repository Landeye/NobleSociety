using System;                                // <-- ADD
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using NobleSociety.Systems.Relations;

namespace NobleSociety.Patches
{
    // Harmony can patch private methods by name.
    [HarmonyPatch(typeof(ChangeRelationAction), "ApplyInternal")]
    internal static class ChangeRelationAction_ApplyInternal_Patch
    {
        [System.ThreadStatic] private static bool _inRipple;

        // Signature must match ILSpy exactly:
        // ApplyInternal(Hero originalHero, Hero originalGainedRelationWith, int relationChange,
        //               bool showQuickNotification, ChangeRelationAction.ChangeRelationDetail detail)
        static void Postfix(
            Hero originalHero,
            Hero originalGainedRelationWith,
            int relationChange,
            bool showQuickNotification,
            ChangeRelationAction.ChangeRelationDetail detail)
        {
            if (_inRipple || originalHero == null || originalGainedRelationWith == null || relationChange == 0)
                return;

            // ONLY ripple for consequential changes (±10 or more)
            if (Math.Abs(relationChange) < 10)
                return;

            _inRipple = true;
            try
            {
                RelationRippleService.ApplyRipples(
                    originalHero, originalGainedRelationWith, relationChange, detail,
                    showSummaryForPlayer: showQuickNotification);
            }
            finally { _inRipple = false; }
        }
    }
}

