using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SlackLineBridge.Models.Configurations
{
    public class SlackChannel
    {
        public string Name { get; set; }
        public string TeamId { get; set; }
        public string ChannelId { get; set; }
        public string WebhookUrl { get; set; }
    }

    public class SlackChannels
    {
        public SlackChannel[] Channels { get; set; }
    }
}
