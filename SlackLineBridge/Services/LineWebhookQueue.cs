using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;

namespace SlackLineBridge.Services
{
    public sealed record LineWebhookRequest(byte[] Body, string Host);

    public sealed class LineWebhookQueue
    {
        public const int DefaultCapacity = 256;

        private readonly Channel<LineWebhookRequest> _channel;

        public LineWebhookQueue(int capacity = DefaultCapacity)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }

            _channel = Channel.CreateBounded<LineWebhookRequest>(new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
        }

        public bool TryEnqueue(LineWebhookRequest request)
        {
            return _channel.Writer.TryWrite(request);
        }

        public IAsyncEnumerable<LineWebhookRequest> ReadAllAsync(CancellationToken cancellationToken)
        {
            return _channel.Reader.ReadAllAsync(cancellationToken);
        }

        public void Complete()
        {
            _channel.Writer.TryComplete();
        }
    }
}
