using System;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.SaveSystem;
using NobleSociety.Extensions;

namespace NobleSociety.State
{
    public enum MemoryType
    {
        ReleasedAfterBattle,
        FavorRefused,
        TradeDeal,
        Insult,
        MilitaryAid,
        Betrayal,
        MarriageProposal,
        SupportInCouncil,
        RejectedCouncilProposal,
        GossipHeard,
        TournamentWin,
        TournamentLoss,
        MinorFavor,
        Murder,
        BattleVictory,
        BattleDefeat,
        LostSoldiersTo,
        ChildBorn,
        TournamentVictory,
        CowardiceInBattle,
        InfluencedBy,
        LostSettlement,
        SiegeStarted,
        BanditThreat,
        Imprisoned
    }

    public enum MemoryTag
    {
        None,
        Gossip,
        BattleVictory,
        BattleDefeat,
        Betrayal,
        Political,
        LiegeMistreatment,
        TradeAgreement,
        Belief
    }

    public class NobleMemoryEntry
    {
        // These fields are serialized via the SaveDefiner
        private MemoryType _type;
        private Hero _source;
        private Hero _target;
        private CampaignTime _timestamp;
        private float _weight;
        private string _notes;
        private List<MemoryTag> _tags = new List<MemoryTag>();
        private float _decayRate = 0.03f;
        private bool _neverForget = false;

        // ✅ Added for OriginalType serialization
        private MemoryType? _originalType;

        private int _repeatCount = 1;
        public int RepeatCount { get => _repeatCount; set => _repeatCount = value; }

        public float DecayRate { get => _decayRate; set => _decayRate = value; }
        public bool NeverForget { get => _neverForget; set => _neverForget = value; }

        public MemoryType Type { get => _type; set => _type = value; }

        public MemoryType? OriginalType
        {
            get => _originalType;
            set => _originalType = value;
        }

        public Hero Source { get => _source; set => _source = value; }
        public Hero Target { get => _target; set => _target = value; }
        public CampaignTime Timestamp { get => _timestamp; set => _timestamp = value; }
        public float Weight { get => _weight; set => _weight = value; }
        public string Notes { get => _notes; set => _notes = value; }
        public List<MemoryTag> Tags { get => _tags; set => _tags = value; }

        public NobleMemoryEntry(MemoryType type, Hero source, Hero target, float weight, string notes = "")
        {
            _type = type;
            _source = source;
            _target = target;
            _weight = weight;
            _notes = notes;
            _timestamp = CampaignTime.Now;

            // Assign decay behavior by memory type
            switch (type)
            {
                case MemoryType.TournamentLoss:
                case MemoryType.MinorFavor:
                    DecayRate = 0.05f; // short-term
                    break;

                case MemoryType.Betrayal:
                case MemoryType.Murder:
                case MemoryType.ChildBorn:
                    NeverForget = true;
                    break;

                case MemoryType.TradeDeal:
                case MemoryType.TournamentWin:
                    DecayRate = 0.025f;
                    break;

                default:
                    DecayRate = 0.03f;
                    break;
            }

        }

        public void DecayWeight(NobleAgentState agent)
        {
            if (NeverForget) return;

            float effectiveDecay = DecayRate * this.GetDecayModifier(agent.Hero);
            if (_weight > 0f)
                _weight -= effectiveDecay;
            else if (_weight < 0f)
                _weight += effectiveDecay;

            if (Math.Abs(_weight) < 0.001f)
                _weight = 0f;
        }

        public bool IsExpired(NobleAgentState agent)
        {
            if (NeverForget)
                return false;

            float daysSince = (float)(CampaignTime.Now.ToDays - _timestamp.ToDays);
            return daysSince * this.GetDecayModifier(agent.Hero) * DecayRate > 1f; // flexible expiration
        }
    }
}