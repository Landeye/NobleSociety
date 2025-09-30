using System.Runtime.CompilerServices;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.BarterSystem.Barterables;

namespace NobleSociety.Patches
{
    internal static class PrisonerBarterCtx
    {
        internal sealed class Ctx
        {
            public Hero Prisoner;
            public Hero CaptorLeader;
        }

        internal static readonly ConditionalWeakTable<SetPrisonerFreeBarterable, Ctx> Map
            = new ConditionalWeakTable<SetPrisonerFreeBarterable, Ctx>();
    }
}
