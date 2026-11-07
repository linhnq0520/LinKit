using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using LinKit.Core.Messaging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace LinKit.Messaging.RabbitMQ;

public sealed class RabbitMqProducer : IBrokerProducer, IDisposable
{
    private static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(5);

    private readonly RabbitMqConnectionFactory _connectionFactory;
    private readonly ConcurrentBag<IModel> _channelPool = new();
    private readonly ExchangeDeclarationCache _declaredExchanges = new();
    private readonly ConditionalWeakTable<IModel, ChannelConfirmState> _confirmStates = new();
    private readonly int _maxPoolSize;
    private readonly string _defaultExchangeType;
    private readonly bool _publisherConfirms;

    public RabbitMqProducer(RabbitMqConnectionFactory connectionFactory, IOptions<RabbitMqOptions>? options = null)
    {
        _connectionFactory = connectionFactory;
        _maxPoolSize = options?.Value.ChannelPoolSize > 0 ? options.Value.ChannelPoolSize : 64;
        _defaultExchangeType = !string.IsNullOrWhiteSpace(options?.Value.DefaultExchangeType)
            ? options.Value.DefaultExchangeType
            : ExchangeType.Topic;
        _publisherConfirms = options?.Value.PublisherConfirms ?? true;
    }

    public async Task ProduceAsync(
        string topicOrExchange,
        string routingKey,
        object message,
        MessageHeaders? headers,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(message);

        var channel = RentChannel();
        var returnToPool = true;
        ChannelConfirmState? confirmState = null;
        ulong sequenceNumber = 0;
        try
        {
            string exchangeType = _defaultExchangeType;
            if (headers is not null && headers.TryGetValue(MessageHeaderNames.ExchangeType, out var extObj) && extObj is string extStr && !string.IsNullOrWhiteSpace(extStr))
            {
                exchangeType = extStr;
            }

            if (!string.IsNullOrEmpty(topicOrExchange) && !_declaredExchanges.IsDeclared(topicOrExchange, exchangeType))
            {
                channel.ExchangeDeclare(exchange: topicOrExchange, type: exchangeType, durable: true);
                _declaredExchanges.MarkDeclared(topicOrExchange, exchangeType);
            }

            var body = LinKitMessageJson.SerializeToUtf8Bytes(message);

            var properties = channel.CreateBasicProperties();
            properties.Persistent = true;

            var messageType = message.GetType().Name;
            var headerDict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                [MessageHeaderNames.MessageType] = messageType,
                [MessageHeaderNames.Timestamp] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            if (headers is not null && headers.Count > 0)
            {
                foreach (var header in headers)
                {
                    if (string.Equals(header.Key, MessageHeaderNames.CorrelationId, StringComparison.OrdinalIgnoreCase) && header.Value != null)
                        properties.CorrelationId = header.Value.ToString();
                    else if (string.Equals(header.Key, MessageHeaderNames.ReplyTo, StringComparison.OrdinalIgnoreCase) && header.Value != null)
                        properties.ReplyTo = header.Value.ToString();
                    else if (string.Equals(header.Key, MessageHeaderNames.MessageId, StringComparison.OrdinalIgnoreCase) && header.Value != null)
                        properties.MessageId = header.Value.ToString();

                    if (header.Value is not null)
                    {
                        headerDict[header.Key] = header.Value;
                    }
                }
            }

            properties.Headers = headerDict;

            TaskCompletionSource<bool>? confirmTcs = null;
            if (_publisherConfirms)
            {
                confirmState = _confirmStates.GetValue(channel, static _ => new ChannelConfirmState());
                sequenceNumber = channel.NextPublishSeqNo;
                confirmTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                // Register before publishing so a fast broker ack cannot race the registration.
                confirmState.Pending[sequenceNumber] = confirmTcs;
            }

            channel.BasicPublish(
                exchange: topicOrExchange ?? string.Empty,
                routingKey: routingKey ?? string.Empty,
                basicProperties: properties,
                body: body
            );

            if (confirmTcs is not null)
            {
                await confirmTcs.Task.WaitAsync(ConfirmTimeout, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            // Channel may be faulted by ExchangeDeclare/BasicPublish or confirm outcome is unknown;
            // discard the channel so a broken channel or late ack cannot corrupt a future publish.
            returnToPool = false;
            throw;
        }
        finally
        {
            confirmState?.Pending.TryRemove(sequenceNumber, out _);
            if (returnToPool)
            {
                ReturnChannel(channel);
            }
            else
            {
                try
                {
                    channel.Dispose();
                }
                catch
                {
                    // Ignore disposal errors on closed channels
                }
            }
        }
    }

    public IModel RentChannel()
    {
        while (_channelPool.TryTake(out var channel))
        {
            if (channel.IsOpen)
            {
                return channel;
            }
            channel.Dispose();
        }

        var newChannel = _connectionFactory.GetConnection().CreateModel();
        if (_publisherConfirms)
        {
            newChannel.ConfirmSelect();
            var state = _confirmStates.GetValue(newChannel, static _ => new ChannelConfirmState());
            newChannel.BasicAcks += (_, e) => CompleteConfirms(state, e.DeliveryTag, e.Multiple, nack: false);
            newChannel.BasicNacks += (_, e) => CompleteConfirms(state, e.DeliveryTag, e.Multiple, nack: true);
            newChannel.ModelShutdown += (_, _) => FailPendingConfirms(state);
        }
        return newChannel;
    }

    public void ReturnChannel(IModel channel)
    {
        if (channel.IsOpen && _channelPool.Count < _maxPoolSize)
        {
            _channelPool.Add(channel);
        }
        else
        {
            try
            {
                channel.Dispose();
            }
            catch
            {
                // Ignore disposal errors on closed channels
            }
        }
    }

    public void Dispose()
    {
        while (_channelPool.TryTake(out var channel))
        {
            try
            {
                channel.Dispose();
            }
            catch
            {
                // Ignore disposal errors
            }
        }
    }

    private static void CompleteConfirms(ChannelConfirmState state, ulong deliveryTag, bool multiple, bool nack)
    {
        if (!multiple)
        {
            if (state.Pending.TryRemove(deliveryTag, out var singleTcs))
            {
                CompleteSingleConfirm(singleTcs, deliveryTag, nack);
            }
            return;
        }

        foreach (var pending in state.Pending)
        {
            if (pending.Key <= deliveryTag && state.Pending.TryRemove(pending.Key, out var tcs))
            {
                CompleteSingleConfirm(tcs, pending.Key, nack);
            }
        }
    }

    private static void CompleteSingleConfirm(TaskCompletionSource<bool> tcs, ulong seqNo, bool nack)
    {
        if (nack)
        {
            tcs.TrySetException(new IOException($"Broker nacked the message with publisher-confirm sequence number {seqNo}."));
        }
        else
        {
            tcs.TrySetResult(true);
        }
    }

    private static void FailPendingConfirms(ChannelConfirmState state)
    {
        foreach (var pending in state.Pending)
        {
            pending.Value.TrySetException(new IOException("Channel closed before the publisher confirm was received."));
        }
    }

    private sealed class ChannelConfirmState
    {
        internal readonly ConcurrentDictionary<ulong, TaskCompletionSource<bool>> Pending = new();
    }
}
