using System;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using NobleSociety.Systems;

namespace NobleSociety.State
{
    public class NobleAgentState
    {
        private readonly Hero _hero;
        private NobleActivityTier _activityTier = NobleActivityTier.Medium;
        private CampaignTime _lastTickTime = CampaignTime.Zero;
        private readonly List<NobleMemoryEntry> _memoryLog = new List<NobleMemoryEntry>();

        public Hero Hero => _hero;
        public NobleActivityTier ActivityTier { get => _activityTier; set => _activityTier = value; }
        public CampaignTime LastTickTime { get => _lastTickTime; set => _lastTickTime = value; }
        public float LastDecayDay { get; set; } = -1f;
        public List<NobleMemoryEntry> MemoryLog => _memoryLog;

        public NobleAgentState(Hero hero) => _hero = hero;

        public bool ShouldTick() => CampaignTime.Now - LastTickTime > CampaignTime.Days(1f);

        public void Tick()
        {
            // Daily gossip attempt
            GossipManager.TryGossip(this);

            // Decay and prune memories
            foreach (var memory in _memoryLog)
                memory.DecayWeight(this);

            _memoryLog.RemoveAll(m => m.IsExpired(this) || Math.Abs(m.Weight) < 0.001f);

            LastTickTime = CampaignTime.Now;
        }
    }

    public enum NobleActivityTier { High, Medium, Low, Dormant }
}
