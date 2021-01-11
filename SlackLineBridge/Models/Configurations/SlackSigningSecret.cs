using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SlackLineBridge.Models.Configurations
{
    public class SlackSigningSecret
    {
        public string Secret { get; }
        public SlackSigningSecret(string secret) => Secret = secret;
    }
}
