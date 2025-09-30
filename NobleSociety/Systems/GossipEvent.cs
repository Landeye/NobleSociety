using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;

namespace NobleSociety.Systems
{
    public class GossipEvent
    {
        public Hero Speaker { get; private set; }
        public Hero Subject { get; private set; }
        public string Topic { get; private set; }
        public string Message { get; private set; }
        public CampaignTime Timestamp { get; private set; }
        public Settlement Location { get; private set; }

        public GossipEvent(Hero speaker, Hero subject, string topic, string message, Settlement location = null)
        {
            Speaker = speaker;
            Subject = subject;
            Topic = topic;
            Message = message;
            Location = location;
            Timestamp = CampaignTime.Now;
        }
    }
}

