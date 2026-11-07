using LinKit.Caching;
using LinKit.Core.Cqrs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MonolithSample.Generated;
using MonolithSample.Modules.Catalog.Commands;
using MonolithSample.Modules.Catalog.Jobs;
using MonolithSample.Modules.Catalog.Queries;
using MonolithSample.Modules.Catalog.Services;
using MonolithSample.Modules.Ordering.Commands;
using MonolithSample.Modules.Ordering.Events;
using Xunit;

namespace LinKit.Messaging.Tests;

public sealed class CqrsDirectDispatchTests
{
    private static ServiceProvider CreateTestServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLinKitCaching();
        services.AddLinKitCqrs();
        services.AddLinKitDependency();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task DirectExtension_SendAsync_WithResult_ExecutesHandlerAndPipelineWithoutBoxing()
    {
        await using var sp = CreateTestServiceProvider();
        using var scope = sp.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var command = new CreateProductCommand("Mechanical Keyboard", 100m, 10m, 5);

        // Compile-time binds directly to MediatorGeneratedExtensions.SendAsync(this IMediator, CreateProductCommand, CancellationToken)
        Guid productId = await mediator.SendAsync(command);

        Assert.NotEqual(Guid.Empty, productId);
    }

    [Fact]
    public async Task DirectExtension_SendAsync_VoidCommand_ExecutesHandlerAndPipeline()
    {
        await using var sp = CreateTestServiceProvider();
        using var scope = sp.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var command = new InventoryAuditJobCommand();

        // Compile-time binds directly to MediatorGeneratedExtensions.SendAsync(this IMediator, InventoryAuditJobCommand, CancellationToken)
        await mediator.SendAsync(command);
    }

    [Fact]
    public async Task DirectExtension_QueryAsync_ExecutesHandlerAndPipeline()
    {
        await using var sp = CreateTestServiceProvider();
        using var scope = sp.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var queryId = Guid.NewGuid();
        var query = new GetProductByIdQuery(queryId);

        // Compile-time binds directly to MediatorGeneratedExtensions.QueryAsync(this IMediator, GetProductByIdQuery, CancellationToken)
        var dto = await mediator.QueryAsync(query);

        Assert.NotNull(dto);
        Assert.Equal(queryId, dto.Id);
    }

    [Fact]
    public async Task DirectExtension_PublishAsync_SequentialAndParallel_ExecutesAllNotificationHandlers()
    {
        await using var sp = CreateTestServiceProvider();
        using var scope = sp.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var evt = new OrderPlacedEvent(Guid.NewGuid(), "user@example.com", 250m);

        // Compile-time binds directly to MediatorGeneratedExtensions.PublishAsync(this IMediator, OrderPlacedEvent, ...)
        await mediator.PublishAsync(evt, PublishStrategy.Sequential);
        await mediator.PublishAsync(evt, PublishStrategy.Parallel);
    }

    [Fact]
    public async Task PolymorphicFallback_WhenCalledViaInterface_DispatchesCorrectly()
    {
        await using var sp = CreateTestServiceProvider();
        using var scope = sp.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        ICommand<Guid> polymorphicCommand = new CreateProductCommand("Mouse", 50m, 5m, 10);
        Guid result = await mediator.SendAsync(polymorphicCommand);
        Assert.NotEqual(Guid.Empty, result);

        ICommand polymorphicVoid = new InventoryAuditJobCommand();
        await mediator.SendAsync(polymorphicVoid);

        var queryId = Guid.NewGuid();
        IQuery<MonolithSample.Modules.Catalog.Models.ProductDto> polymorphicQuery = new GetProductByIdQuery(queryId);
        var dto = await mediator.QueryAsync(polymorphicQuery);
        Assert.Equal(queryId, dto.Id);
    }

    [Fact]
    public async Task CustomMediatorWrapper_FallsBackToInterfaceImplementationGracefully()
    {
        await using var sp = CreateTestServiceProvider();
        using var scope = sp.CreateScope();
        var innerMediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var wrappedMediator = new TrackingMediatorDecorator(innerMediator);

        // Calling direct extension on a custom IMediator decorator falls back cleanly to interface call
        var result = await wrappedMediator.SendAsync(new CreateProductCommand("Monitor", 300m, 0m, 2));

        Assert.NotEqual(Guid.Empty, result);
        Assert.Equal(1, wrappedMediator.SendWithResultCount);
    }

    private sealed class TrackingMediatorDecorator(IMediator inner) : IMediator
    {
        public int SendWithResultCount { get; private set; }

        public Task DispatchAsync(ICommand command, CancellationToken cancellationToken = default)
            => inner.DispatchAsync(command, cancellationToken);

        public Task<TResponse> DispatchAsync<TResponse>(ICommand<TResponse> command, CancellationToken cancellationToken = default)
        {
            SendWithResultCount++;
            return inner.DispatchAsync(command, cancellationToken);
        }

        public Task<TResponse> DispatchQueryAsync<TResponse>(IQuery<TResponse> query, CancellationToken cancellationToken = default)
            => inner.DispatchQueryAsync(query, cancellationToken);

        public Task DispatchNotificationAsync<TNotification>(TNotification notification, PublishStrategy strategy = PublishStrategy.Sequential, CancellationToken ct = default)
            where TNotification : INotification
            => inner.DispatchNotificationAsync(notification, strategy, ct);
    }
}
