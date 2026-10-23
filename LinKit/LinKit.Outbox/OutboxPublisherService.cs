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

public class OutboxPublisherService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<OutboxPublisherService> _logger;
    private readonly OutboxOptions _options;
    private readonly TimeProvider _time;

    public OutboxPublisherService(
        IServiceProvider serviceProvider,
        ILogger<OutboxPublisherService> logger,
        OutboxOptions? options = null,
        TimeProvider? timeProvider = null)
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
        return string.Create(messageType.Length - start, (messageType, start), static (span, state) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                var c = state.messageType[state.start + i];
                span[i] = c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-' or '.' ? c : '_';
            }
        });
    }

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
                    var pendingMessages = await outboxStore.ClaimPendingAsync(_options.BatchSize, _options.LeaseDuration, stoppingToken);
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

                            string topic = headers.DestinationTopic ?? string.Empty;
                            if (string.IsNullOrWhiteSpace(topic))
                            {
                                topic = SanitizeTopic(msg.MessageType);
                            }
                            using var payload = JsonDocument.Parse(msg.Payload);
                            await producer.ProduceAsync(topic, topic, payload.RootElement, headers, stoppingToken);
                            await outboxStore.MarkProcessedAsync(msg.Id, stoppingToken);

                            _logger.LogInformation("Dispatched outbox message {MessageId} ({MessageType})", msg.Id, msg.MessageType);
                        }
                        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                        {
                            var attempt = msg.RetryCount + 1;
                            if (attempt >= _options.MaxRetries)
                                _logger.LogCritical(ex, "Outbox message {MessageId} dead-lettered after {Attempts} attempts", msg.Id, attempt);
                            else
                                _logger.LogError(ex, "Failed to dispatch outbox message {MessageId} (attempt {Attempt})", msg.Id, attempt);

                            var next = _time.GetUtcNow().UtcDateTime + _options.GetRetryDelay(attempt);
                            await outboxStore.MarkFailedAsync(msg.Id, ex.Message, next, stoppingToken);
                        }
                    }
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Error processing outbox messages");
            }

            // A full batch means more work is likely pending; skip the idle delay to keep throughput high.
            if (drainedFullBatch) continue;

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

public static class OutboxServiceCollectionExtensions
{
    private static void AddCommon(IServiceCollection services, Action<OutboxOptions>? configure)
    {
        var options = new OutboxOptions();
        configure?.Invoke(options);
        options.Validate();
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton(services, options);
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton(services, TimeProvider.System);
    }

    /// <summary>
    /// Registers in-memory transactional outbox and idempotent inbox stores.
    /// NOTE: In-memory store is intended for development and testing. Production deployments should register a durable store.
    /// </summary>
    public static IServiceCollection AddLinKitOutbox(this IServiceCollection services, Action<OutboxOptions>? configure = null)
    {
        AddCommon(services, configure);
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton<IOutboxStore, InMemoryOutboxStore>(services);
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton<IInboxStore, InMemoryInboxStore>(services);
        services.AddHostedService<OutboxPublisherService>();
        return services;
    }

    /// <summary>
    /// Registers custom durable outbox and inbox stores.
    /// </summary>
    public static IServiceCollection AddLinKitOutbox<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TOutboxStore,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TInboxStore>(
        this IServiceCollection services,
        Action<OutboxOptions>? configure = null)
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
