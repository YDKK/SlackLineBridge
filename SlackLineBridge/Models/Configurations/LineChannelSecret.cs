using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SlackLineBridge.Models.Configurations
{
    public class LineChannelSecret
    {
        public string Secret { get; }
        public LineChannelSecret(string secret) => Secret = secret;
    }
}
