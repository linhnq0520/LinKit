using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using LinKit.Core.Messaging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace LinKit.Messaging.RabbitMQ;

/// <summary>
/// Produces messages to RabbitMQ using a pooled set of publisher channels and optional publisher confirms.
/// </summary>
/// <remarks>
/// Exchanges are declared lazily and cached per <c>(name, type)</c> pair. When <see cref="RabbitMqOptions.PublisherConfirms"/>
/// is enabled the call waits for the broker acknowledgement, so a negative acknowledgement or a timeout surfaces as an
/// exception instead of a silently dropped message. A channel whose publish failed is discarded rather than returned to
/// the pool.
/// </remarks>
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

    /// <summary>Creates the producer.</summary>
    /// <param name="connectionFactory">Factory that supplies the shared AMQP connection.</param>
    /// <param name="options">Pool size, default exchange type and confirm settings; defaults are used when omitted.</param>
    public RabbitMqProducer(
        RabbitMqConnectionFactory connectionFactory,
        IOptions<RabbitMqOptions>? options = null
    )
    {
        _connectionFactory = connectionFactory;
        _maxPoolSize = options?.Value.ChannelPoolSize > 0 ? options.Value.ChannelPoolSize : 64;
        _defaultExchangeType = !string.IsNullOrWhiteSpace(options?.Value.DefaultExchangeType)
            ? options.Value.DefaultExchangeType
            : ExchangeType.Topic;
        _publisherConfirms = options?.Value.PublisherConfirms ?? true;
    }

    /// <summary>Serializes <paramref name="message"/> and publishes it to the exchange.</summary>
    /// <param name="topicOrExchange">Exchange to publish to; an empty value uses the broker's default exchange.</param>
    /// <param name="routingKey">Routing key used by the exchange type, or the queue name for the default exchange.</param>
    /// <param name="message">Payload, serialized with <see cref="LinKitMessageJson"/>.</param>
    /// <param name="headers">Optional headers; recognizable entries such as correlation id and reply-to are mapped onto the
    /// AMQP basic properties, and an exchange type override can be supplied through
    /// <see cref="MessageHeaderNames.ExchangeType"/>.</param>
    /// <param name="cancellationToken">Token used to abort the publish and the confirm wait.</param>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is <see langword="null"/>.</exception>
    /// <exception cref="System.IO.IOException">The broker negatively acknowledged the message, the channel closed before the
    /// confirm arrived, or the confirm timed out.</exception>
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
            if (
                headers is not null
                && headers.TryGetValue(MessageHeaderNames.ExchangeType, out var extObj)
                && extObj is string extStr
                && !string.IsNullOrWhiteSpace(extStr)
            )
            {
                exchangeType = extStr;
            }

            if (
                !string.IsNullOrEmpty(topicOrExchange)
                && !_declaredExchanges.IsDeclared(topicOrExchange, exchangeType)
            )
            {
                channel.ExchangeDeclare(
                    exchange: topicOrExchange,
                    type: exchangeType,
                    durable: true
                );
                _declaredExchanges.MarkDeclared(topicOrExchange, exchangeType);
            }

            var body = LinKitMessageJson.SerializeToUtf8Bytes(message);

            var properties = channel.CreateBasicProperties();
            properties.Persistent = true;

            var messageType = message.GetType().Name;
            var headerDict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                [MessageHeaderNames.MessageType] = messageType,
                [MessageHeaderNames.Timestamp] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };

            if (headers is not null && headers.Count > 0)
            {
                foreach (var header in headers)
                {
                    if (
                        string.Equals(
                            header.Key,
                            MessageHeaderNames.CorrelationId,
                            StringComparison.OrdinalIgnoreCase
                        )
                        && header.Value != null
                    )
                        properties.CorrelationId = header.Value.ToString();
                    else if (
                        string.Equals(
                            header.Key,
                            MessageHeaderNames.ReplyTo,
                            StringComparison.OrdinalIgnoreCase
                        )
                        && header.Value != null
                    )
                        properties.ReplyTo = header.Value.ToString();
                    else if (
                        string.Equals(
                            header.Key,
                            MessageHeaderNames.MessageId,
                            StringComparison.OrdinalIgnoreCase
                        )
                        && header.Value != null
                    )
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
                confirmState = _confirmStates.GetValue(
                    channel,
                    static _ => new ChannelConfirmState()
                );
                sequenceNumber = channel.NextPublishSeqNo;
                confirmTcs = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                );
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
                await confirmTcs
                    .Task.WaitAsync(ConfirmTimeout, cancellationToken)
                    .ConfigureAwait(false);
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

    /// <summary>Rents an open channel from the pool, creating and arming a new one when none is available.</summary>
    /// <returns>An open channel; the caller must return it through <see cref="ReturnChannel"/>.</returns>
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
            newChannel.BasicAcks += (_, e) =>
                CompleteConfirms(state, e.DeliveryTag, e.Multiple, nack: false);
            newChannel.BasicNacks += (_, e) =>
                CompleteConfirms(state, e.DeliveryTag, e.Multiple, nack: true);
            newChannel.ModelShutdown += (_, _) => FailPendingConfirms(state);
        }
        return newChannel;
    }

    /// <summary>Returns a channel to the pool, or disposes it when it is closed or the pool is full.</summary>
    /// <param name="channel">Channel previously obtained from <see cref="RentChannel"/>.</param>
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

    /// <summary>Disposes every pooled channel; the producer is unusable afterwards.</summary>
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

    private static void CompleteConfirms(
        ChannelConfirmState state,
        ulong deliveryTag,
        bool multiple,
        bool nack
    )
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

    private static void CompleteSingleConfirm(
        TaskCompletionSource<bool> tcs,
        ulong seqNo,
        bool nack
    )
    {
        if (nack)
        {
            tcs.TrySetException(
                new IOException(
                    $"Broker nacked the message with publisher-confirm sequence number {seqNo}."
                )
            );
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
            pending.Value.TrySetException(
                new IOException("Channel closed before the publisher confirm was received.")
            );
        }
    }

    private sealed class ChannelConfirmState
    {
        internal readonly ConcurrentDictionary<ulong, TaskCompletionSource<bool>> Pending = new();
    }
}
