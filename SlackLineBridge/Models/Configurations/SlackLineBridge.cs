using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SlackLineBridge.Models.Configurations
{
    public class SlackLineBridge
    {
        public string Slack { get; set; }
        public string Line { get; set; }
    }

    public class SlackLineBridges
    {
        public SlackLineBridge[] Bridges { get; set; }
    }
}
