using System;

namespace SlackLineBridge.Models.Configurations
{
    public class LineChannelSecret
    {
        public string Secret { get; }
        public LineChannelSecret(string secret)
        {
            if (string.IsNullOrWhiteSpace(secret))
            {
                throw new ArgumentException("LINE channel secret must be configured.", nameof(secret));
            }

            Secret = secret;
        }
    }
}
