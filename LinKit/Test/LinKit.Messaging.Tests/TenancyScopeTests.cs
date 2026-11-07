using System.Net;
using LinKit.Core.Messaging;
using LinKit.Outbox;
using LinKit.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LinKit.Messaging.Tests;

/// <summary>
/// Covers the Phase 1.8 tenancy contract: the ambient tenant is never created implicitly, scopes always restore the
/// previous value, and the tenant survives the Outbox transaction boundary through the message headers.
/// </summary>
public sealed class TenancyScopeTests
{
    private sealed record TenantProbe(string OrderId);

    [Fact]
    public void AMissingTenantIsObservable()
    {
        var accessor = new TenantAccessor();

        Assert.Null(accessor.Current);
        Assert.False(accessor.TryGet(out _));

        var error = Assert.Throws<InvalidOperationException>(() => accessor.Require());
        Assert.Contains("No ambient tenant", error.Message);
    }

    [Fact]
    public void BeginScopeResetsToNullOnDispose()
    {
        var accessor = new TenantAccessor();

        using (accessor.BeginScope("tenant-a"))
        {
            Assert.True(accessor.TryGet(out var current));
            Assert.Equal("tenant-a", current.TenantId);
        }

        Assert.Null(accessor.Current);
        Assert.False(accessor.TryGet(out _));
    }

    [Fact]
    public void NestedScopesRestoreTheOuterTenant()
    {
        var accessor = new TenantAccessor();

        using var outer = accessor.BeginScope("tenant-a", "Tenant A");
        using (accessor.BeginScope("tenant-b"))
        {
            Assert.Equal("tenant-b", accessor.Require().TenantId);
        }

        Assert.Equal("tenant-a", accessor.Require().TenantId);
        Assert.Equal("Tenant A", accessor.Require().TenantName);
    }

    [Fact]
    public void DisposingAScopeTwiceDoesNotDropTheOuterTenant()
    {
        var accessor = new TenantAccessor();

        using var outer = accessor.BeginScope("tenant-a");
        var inner = accessor.BeginScope("tenant-b");
        inner.Dispose();
        inner.Dispose();

        Assert.Equal("tenant-a", accessor.Require().TenantId);
    }

    [Fact]
    public void EmptyTenantIdIsRejected()
    {
        var accessor = new TenantAccessor();

        Assert.Throws<ArgumentException>(() => accessor.BeginScope(" "));
    }

    [Fact]
    public void OutboxMessageCapturesTheAmbientTenant()
    {
        var accessor = new TenantAccessor();

        var withoutScope = OutboxMessage.Create(new TenantProbe("a"), "{}");
        Assert.False(withoutScope.Headers!.ContainsKey(MessageHeaderNames.TenantId));

        using (accessor.BeginScope("tenant-a"))
        {
            var message = OutboxMessage.Create(new TenantProbe("b"), "{}");

            Assert.Equal("tenant-a", message.Headers![MessageHeaderNames.TenantId]);
            Assert.Equal("tenant-a", message.Headers!.TenantId);
        }
    }

    [Fact]
    public void RestoringATenantFromHeadersLeavesThePreviousTenantIntact()
    {
        var accessor = new TenantAccessor();
        var interceptor = new TenantConsumeInterceptor(accessor);
        var services = new ServiceCollection()
            .AddSingleton<IMessageConsumeInterceptor>(interceptor)
            .BuildServiceProvider();
        var headers = new Dictionary<string, object> { [MessageHeaderNames.TenantId] = "tenant-b" };

        using (accessor.BeginScope("tenant-a"))
        {
            using (MessageConsumeInterceptors.Enter(services, headers))
            {
                Assert.Equal("tenant-b", accessor.Require().TenantId);
            }

            Assert.Equal("tenant-a", accessor.Require().TenantId);
        }

        Assert.Null(accessor.Current);
    }

    [Fact]
    public async Task TenantIsResolvedThroughDependencyInjection()
    {
        await using var provider = new ServiceCollection()
            .AddLinKitTenancy()
            .BuildServiceProvider();
        var accessor = provider.GetRequiredService<ITenantAccessor>();

        Assert.Throws<InvalidOperationException>(() =>
            provider.GetRequiredService<ITenantContext>()
        );

        using (accessor.BeginScope("tenant-a"))
        {
            await using var scope = provider.CreateAsyncScope();
            Assert.Equal(
                "tenant-a",
                scope.ServiceProvider.GetRequiredService<ITenantContext>().TenantId
            );
        }
    }

    [Fact]
    public async Task OutboundHttpCallsCarryTheTenantHeader()
    {
        var accessor = new TenantAccessor();
        var capture = new HeaderCapturingHandler();
        using var propagation = new TenantPropagationHandler(accessor) { InnerHandler = capture };
        using var client = new HttpClient(propagation)
        {
            BaseAddress = new Uri("http://inventory.local/"),
        };

        using (accessor.BeginScope("tenant-a"))
        {
            using var response = await client.GetAsync("/stock");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal("tenant-a", capture.LastTenantHeader);

        using (await client.GetAsync("/stock"))
        {
            // No scope means no header: a missing tenant never inherits the previous caller's one.
        }

        Assert.Null(capture.LastTenantHeader);
    }

    private sealed class HeaderCapturingHandler : HttpMessageHandler
    {
        public string? LastTenantHeader { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            LastTenantHeader = request.Headers.TryGetValues(
                TenantPropagation.TenantHeaderName,
                out var values
            )
                ? values.FirstOrDefault()
                : null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
