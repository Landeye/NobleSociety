using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using NobleSociety.State;
using NSLog = NobleSociety.Logging.FileLogger;

namespace NobleSociety
{
    public static class NobleSocietyManager
    {
        private static readonly Dictionary<Hero, NobleAgentState> _agentStates
            = new Dictionary<Hero, NobleAgentState>(4096);

        public static NobleAgentState GetOrCreateAgent(Hero hero)
        {
            if (hero == null) return null;

            if (!_agentStates.TryGetValue(hero, out var agent))
            {
                agent = new NobleAgentState(hero);
                _agentStates[hero] = agent;
            }
            return agent;
        }

        public static List<NobleAgentState> GetAllAgents()
            => _agentStates.Values.ToList();

        /// <summary>
        /// Adds a new memory entry for <paramref name="source"/>.
        /// If it's first-hand (source experienced it), we optionally tag it as a belief.
        /// </summary>
        public static void RegisterMemory(
            Hero source,
            Hero target,
            MemoryType type,
            float weight,
            string notes = "",
            MemoryTag tag = MemoryTag.None,
            bool markFirstHandAsBelief = true)
        {
            var agent = GetOrCreateAgent(source);
            if (agent == null) return;

            var memory = new NobleMemoryEntry(type, source, target, weight, notes);
            if (tag != MemoryTag.None)
                memory.Tags.Add(tag);

            agent.MemoryLog.Add(memory);
            NSLog.Log($"[MEMORY] {source?.Name} recorded {type} (Weight={weight:0.00}) Notes='{notes}'");

            // Promote to belief if first-hand (optional)
            if (markFirstHandAsBelief)
            {
                var belief = new NobleMemoryEntry(type, source, target, weight, notes);
                belief.Tags.Add(MemoryTag.Belief);
                agent.MemoryLog.Add(belief);
                // NSLog.Log($"[BELIEF] {source?.Name} auto-believes their own {type}.");
            }
        }
    }
}
