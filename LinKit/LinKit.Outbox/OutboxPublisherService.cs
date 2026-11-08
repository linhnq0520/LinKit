using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LinKit.Core.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LinKit.Outbox;

/// <summary>
/// Background worker that drains the outbox and publishes each stored message through
/// <see cref="IBrokerProducer"/>. Claiming uses a lease so several instances can run the same loop without
/// double-publishing, and failures are retried with exponential backoff before being dead-lettered.
/// </summary>
/// <remarks>
/// The service creates a new DI scope per poll, which means <see cref="IOutboxStore"/> and
/// <see cref="IBrokerProducer"/> may be scoped. The ambient tenant captured at save time is restored for the duration
/// of each dispatch, so interceptors observe the original tenant instead of the publisher's.
/// </remarks>
public class OutboxPublisherService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<OutboxPublisherService> _logger;
    private readonly OutboxOptions _options;
    private readonly TimeProvider _time;

    /// <summary>Creates the publisher loop.</summary>
    /// <param name="serviceProvider">Root provider used to create one scope per poll.</param>
    /// <param name="logger">Logger for dispatch, retry and dead-letter events.</param>
    /// <param name="options">Polling, batching and retry settings; defaults are used when omitted.</param>
    /// <param name="timeProvider">Clock used for backoff scheduling; defaults to <see cref="TimeProvider.System"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">The supplied options fail <see cref="OutboxOptions.Validate"/>.</exception>
    public OutboxPublisherService(
        IServiceProvider serviceProvider,
        ILogger<OutboxPublisherService> logger,
        OutboxOptions? options = null,
        TimeProvider? timeProvider = null
    )
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _options = options ?? new OutboxOptions();
        _options.Validate();
        _time = timeProvider ?? TimeProvider.System;
    }

    private static string SanitizeTopic(string messageType)
    {
        var start = messageType.LastIndexOf('.') + 1;
        return string.Create(
            messageType.Length - start,
            (messageType, start),
            static (span, state) =>
            {
                for (var i = 0; i < span.Length; i++)
                {
                    var c = state.messageType[state.start + i];
                    span[i] = c
                        is (>= 'a' and <= 'z')
                            or (>= 'A' and <= 'Z')
                            or (>= '0' and <= '9')
                            or '_'
                            or '-'
                            or '.'
                        ? c
                        : '_';
                }
            }
        );
    }

    /// <summary>
    /// Runs the claim-publish loop until the host stops. Each iteration claims at most
    /// <see cref="OutboxOptions.BatchSize"/> messages, publishes them in FIFO order and updates their state; a full
    /// batch is drained back-to-back without waiting.
    /// </summary>
    /// <param name="stoppingToken">Signalled when the host is shutting down; in-flight messages are left leased so
    /// another instance (or the lease timeout) can resume them.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OutboxPublisherService started");

        while (!stoppingToken.IsCancellationRequested)
        {
            var drainedFullBatch = false;
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var outboxStore = scope.ServiceProvider.GetService<IOutboxStore>();
                var producer = scope.ServiceProvider.GetService<IBrokerProducer>();

                if (outboxStore != null && producer != null)
                {
                    var pendingMessages = await outboxStore.ClaimPendingAsync(
                        _options.BatchSize,
                        _options.LeaseDuration,
                        stoppingToken
                    );
                    drainedFullBatch = pendingMessages.Count >= _options.BatchSize;

                    foreach (var msg in pendingMessages)
                    {
                        if (stoppingToken.IsCancellationRequested)
                            break;

                        try
                        {
                            var headers = new MessageHeaders(msg.Headers);
                            headers.MessageType = msg.MessageType;
                            headers.TryAdd(MessageHeaderNames.PartitionKey, msg.Id.ToString("N"));
                            headers.TryAdd(MessageHeaderNames.MessageId, msg.Id.ToString("N"));

                            string topic = headers.DestinationTopic ?? string.Empty;
                            if (string.IsNullOrWhiteSpace(topic))
                            {
                                topic = SanitizeTopic(msg.MessageType);
                            }

                            // A [Message] attribute (or an explicit header) can pin the routing key independently of the topic.
                            var routingKey = headers.RoutingKey
                                is { Length: > 0 } explicitRoutingKey
                                ? explicitRoutingKey
                                : topic;

                            // Restore the ambient state captured at save time (tenant id, and any other registered
                            // interceptor) so nothing published for one tenant runs as another.
                            using var messageScope = MessageConsumeInterceptors.Enter(
                                scope.ServiceProvider,
                                headers
                            );
                            using var payload = JsonDocument.Parse(msg.Payload);
                            await producer.ProduceAsync(
                                topic,
                                routingKey,
                                payload.RootElement,
                                headers,
                                stoppingToken
                            );
                            await outboxStore.MarkProcessedAsync(msg.Id, stoppingToken);

                            _logger.LogInformation(
                                "Dispatched outbox message {MessageId} ({MessageType})",
                                msg.Id,
                                msg.MessageType
                            );
                        }
                        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                        {
                            var attempt = msg.RetryCount + 1;
                            if (attempt >= _options.MaxRetries)
                                _logger.LogCritical(
                                    ex,
                                    "Outbox message {MessageId} dead-lettered after {Attempts} attempts",
                                    msg.Id,
                                    attempt
                                );
                            else
                                _logger.LogError(
                                    ex,
                                    "Failed to dispatch outbox message {MessageId} (attempt {Attempt})",
                                    msg.Id,
                                    attempt
                                );

                            var next =
                                _time.GetUtcNow().UtcDateTime + _options.GetRetryDelay(attempt);
                            await outboxStore.MarkFailedAsync(
                                msg.Id,
                                ex.Message,
                                next,
                                stoppingToken
                            );
                        }
                    }
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Error processing outbox messages");
            }

            // A full batch means more work is likely pending; skip the idle delay to keep throughput high.
            if (drainedFullBatch)
                continue;

            try
            {
                await Task.Delay(_options.PollInterval, _time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("OutboxPublisherService stopped");
    }
}

/// <summary>
/// Registration helpers for the transactional outbox and idempotent inbox.
/// </summary>
public static class OutboxServiceCollectionExtensions
{
    private static void AddCommon(IServiceCollection services, Action<OutboxOptions>? configure)
    {
        var options = new OutboxOptions();
        configure?.Invoke(options);
        options.Validate();
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton(
            services,
            options
        );
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton(
            services,
            TimeProvider.System
        );
    }

    /// <summary>
    /// Registers in-memory transactional outbox and idempotent inbox stores.
    /// NOTE: In-memory store is intended for development and testing. Production deployments should register a durable store.
    /// </summary>
    /// <param name="services">Service collection to configure.</param>
    /// <param name="configure">Optional callback that adjusts <see cref="OutboxOptions"/> before validation.</param>
    /// <returns>The same collection, to allow call chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The configured options fail <see cref="OutboxOptions.Validate"/>.</exception>
    public static IServiceCollection AddLinKitOutbox(
        this IServiceCollection services,
        Action<OutboxOptions>? configure = null
    )
    {
        AddCommon(services, configure);
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton<
            IOutboxStore,
            InMemoryOutboxStore
        >(services);
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton<
            IInboxStore,
            InMemoryInboxStore
        >(services);
        services.AddHostedService<OutboxPublisherService>();
        return services;
    }

    /// <summary>
    /// Registers custom durable outbox and inbox stores.
    /// </summary>
    /// <typeparam name="TOutboxStore">Store implementation resolving to a durable backend.</typeparam>
    /// <typeparam name="TInboxStore">Inbox ledger implementation resolving to a durable backend.</typeparam>
    /// <param name="services">Service collection to configure.</param>
    /// <param name="configure">Optional callback that adjusts <see cref="OutboxOptions"/> before validation.</param>
    /// <returns>The same collection, to allow call chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The configured options fail <see cref="OutboxOptions.Validate"/>.</exception>
    public static IServiceCollection AddLinKitOutbox<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
            TOutboxStore,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TInboxStore
    >(this IServiceCollection services, Action<OutboxOptions>? configure = null)
        where TOutboxStore : class, IOutboxStore
        where TInboxStore : class, IInboxStore
    {
        AddCommon(services, configure);
        services.AddScoped<IOutboxStore, TOutboxStore>();
        services.AddScoped<IInboxStore, TInboxStore>();
        services.AddHostedService<OutboxPublisherService>();
        return services;
    }
}
