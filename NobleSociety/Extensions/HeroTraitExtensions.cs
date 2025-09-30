using NobleSociety.State;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CharacterDevelopment;

namespace NobleSociety.Extensions
{
    public struct HeroTraitSnapshot
    {
        public int Calculating;
        public int Generosity;
        public int Honor;
        public int Mercy;
        public int Valor;
    }

    public static class HeroTraitExtensions
    {
        public static HeroTraitSnapshot GetHeroTraits(this Hero hero)
        {
            return new HeroTraitSnapshot
            {
                Calculating = hero.GetTraitLevel(DefaultTraits.Calculating),
                Generosity = hero.GetTraitLevel(DefaultTraits.Generosity),
                Honor = hero.GetTraitLevel(DefaultTraits.Honor),
                Mercy = hero.GetTraitLevel(DefaultTraits.Mercy),
                Valor = hero.GetTraitLevel(DefaultTraits.Valor)
            };
        }

        public static float GetDecayModifier(this NobleMemoryEntry memory, Hero hero)
        {
            var traits = hero.GetHeroTraits();
            float modifier = 1f;

            if (traits.Mercy > 0)
                modifier *= 1.25f;
            if (traits.Honor < 0)
                modifier *= 0.85f;
            if (traits.Generosity > 0)
                modifier *= 0.9f;
            if (traits.Calculating > 0)
                modifier *= 1.1f;

            return modifier;
        }

        public static int GetTraitValue(this NobleAgentState agent, TraitObject trait)
        {
            return agent.Hero?.GetTraitLevel(trait) ?? 0;
        }
    }
}