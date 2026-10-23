using LinKit.Outbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LinKit.Outbox.Data;

public static class AdoNetOutboxServiceCollectionExtensions
{
    public static IServiceCollection AddLinKitAdoNetOutbox(
        this IServiceCollection services,
        Action<OutboxOptions>? configure = null)
    {
        services.AddHostedService<AdoNetOutboxSchemaInitializer>();
        return services.AddLinKitOutbox<AdoNetOutboxStore, AdoNetInboxStore>(configure);
    }
}

internal sealed class AdoNetOutboxSchemaInitializer : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AdoNetOutboxSchemaInitializer> _logger;

    public AdoNetOutboxSchemaInitializer(
        IServiceScopeFactory scopeFactory,
        ILogger<AdoNetOutboxSchemaInitializer> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        var inbox = scope.ServiceProvider.GetRequiredService<IInboxStore>();
        if (outbox is not IAdoNetSchemaStore outboxSchema || inbox is not IAdoNetSchemaStore inboxSchema)
            throw new InvalidOperationException("AdoNetOutboxSchemaInitializer requires the registered ADO.NET outbox and inbox stores.");
        await outboxSchema.EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await inboxSchema.EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("LinKit ADO.NET outbox and inbox schemas are ready.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
