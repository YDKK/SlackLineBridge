using System;

namespace SlackLineBridge.Models.Configurations
{
    public class SlackSigningSecret
    {
        public string Secret { get; }
        public SlackSigningSecret(string secret)
        {
            if (string.IsNullOrWhiteSpace(secret))
            {
                throw new ArgumentException("Slack signing secret must be configured.", nameof(secret));
            }

            Secret = secret;
        }
    }
}
