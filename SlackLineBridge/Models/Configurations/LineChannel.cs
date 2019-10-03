using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SlackLineBridge.Models.Configurations
{
    public class LineChannel
    {
        public string Name { get; set; }
        public string Id { get; set; }
    }

    public class LineChannels
    {
        public LineChannel[] Channels { get; set; }
    }
}
