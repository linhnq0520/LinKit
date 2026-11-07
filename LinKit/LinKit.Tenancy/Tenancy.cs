using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http;
using System.Threading;
using LinKit.Core.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LinKit.Tenancy;

public interface ITenantContext
{
    string? TenantId { get; set; }
    string? TenantName { get; set; }
    IDictionary<string, object> Items { get; }
}

public class TenantContext : ITenantContext
{
    public string? TenantId { get; set; }
    public string? TenantName { get; set; }
    public IDictionary<string, object> Items { get; } = new ConcurrentDictionary<string, object>();
}

/// <summary>
/// A resolved tenant that lives until it is disposed. Disposing restores whatever tenant was ambient before the
/// scope started (which is <see langword="null"/> at the outermost level), so a tenant can never outlive its work.
/// </summary>
public interface ITenantScope : IDisposable
{
    ITenantContext Context { get; }
}

/// <summary>
/// Access to the ambient tenant. A missing tenant is observable: <see cref="TryGet"/> reports it without side
/// effects and <see cref="Require"/> throws, instead of silently handing out an empty context.
/// </summary>
public interface ITenantAccessor
{
    /// <summary>The ambient tenant, or <see langword="null"/> when no scope is active.</summary>
    ITenantContext? Current { get; }

    /// <summary>
    /// Returns <see langword="true"/> when an ambient tenant exists. Prefer this in code that can work without a
    /// tenant; use <see cref="Require"/> where a tenant is mandatory.
    /// </summary>
    bool TryGet([NotNullWhen(true)] out ITenantContext? context);

    /// <summary>
    /// The ambient tenant. Throws <see cref="InvalidOperationException"/> when no scope is active, so a missing
    /// tenant fails loudly instead of writing unpartitioned data.
    /// </summary>
    ITenantContext Require();

    /// <summary>Establishes <paramref name="tenantId"/> as the ambient tenant until the scope is disposed.</summary>
    ITenantScope BeginScope(string tenantId, string? tenantName = null);

    /// <summary>Establishes <paramref name="context"/> as the ambient tenant until the scope is disposed.</summary>
    ITenantScope BeginScope(ITenantContext context);
}

/// <summary>
/// Async-flow tenant accessor. The ambient value lives in an <see cref="AsyncLocal{T}"/> so it flows into awaited
/// work and is isolated per request. Nothing is created implicitly: without a scope the ambient tenant is
/// <see langword="null"/>.
/// </summary>
public sealed class TenantAccessor : ITenantAccessor
{
    private static readonly AsyncLocal<ITenantContext?> _current = new();

    /// <summary>
    /// The ambient tenant of the current async flow, or <see langword="null"/>. Exposed statically because the
    /// backing store is async-local; this lets infrastructure without a service provider (for example Outbox
    /// message construction) observe the same tenant as the DI-registered accessor.
    /// </summary>
    public static ITenantContext? Ambient => _current.Value;

    public ITenantContext? Current => _current.Value;

    public bool TryGet([NotNullWhen(true)] out ITenantContext? context)
    {
        context = _current.Value;
        return context is not null;
    }

    public ITenantContext Require() =>
        _current.Value
        ?? throw new InvalidOperationException(
            "No ambient tenant is available. Establish one with ITenantAccessor.BeginScope(tenantId) (or the "
                + "tenant scope middleware) before running tenant-scoped work, or use TryGet when a tenant is optional."
        );

    public ITenantScope BeginScope(string tenantId, string? tenantName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        return BeginScope(new TenantContext { TenantId = tenantId, TenantName = tenantName });
    }

    public ITenantScope BeginScope(ITenantContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var scope = new Scope(_current.Value, context);
        _current.Value = context;
        return scope;
    }

    private sealed class Scope(ITenantContext? previous, ITenantContext context) : ITenantScope
    {
        private int _disposed;

        public ITenantContext Context => context;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _current.Value = previous;
            }
        }
    }
}

public static class TenantPropagation
{
    /// <summary>HTTP header used to carry the tenant id between LinKit services.</summary>
    public const string TenantHeaderName = "x-tenant-id";

    /// <summary>Broker header used to carry the tenant id. Shares the name declared by <see cref="MessageHeaderNames"/>.</summary>
    public const string TenantMessageHeader = MessageHeaderNames.TenantId;

    public static void Inject(MessageHeaders headers, string? tenantId)
    {
        ArgumentNullException.ThrowIfNull(headers);

        if (string.IsNullOrEmpty(tenantId))
        {
            headers.Remove(TenantMessageHeader);
            return;
        }

        headers[TenantMessageHeader] = tenantId;
    }

    /// <summary>Copies the ambient tenant (when present) into <paramref name="headers"/>.</summary>
    public static void InjectAmbientTenant(MessageHeaders headers) =>
        Inject(headers, TenantAccessor.Ambient?.TenantId);

    public static string? Extract(IReadOnlyDictionary<string, object> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        return headers.TryGetValue(TenantMessageHeader, out var val) ? val?.ToString() : null;
    }
}

/// <summary>
/// Restores the tenant id from the message header for the duration of one consumed message. The header is trusted
/// as broker-internal metadata; do not accept it from untrusted publishers.
/// </summary>
public sealed class TenantConsumeInterceptor(ITenantAccessor accessor) : IMessageConsumeInterceptor
{
    public IDisposable? Enter(
        IServiceProvider scopedServices,
        IReadOnlyDictionary<string, object> headers
    )
    {
        var tenantId = TenantPropagation.Extract(headers);

        // A message published without a tenant must not inherit the tenant of the previous message on this loop,
        // so an empty context is established deliberately rather than leaving the previous scope in place.
        return accessor.BeginScope(new TenantContext { TenantId = tenantId ?? string.Empty });
    }
}

/// <summary>
/// Propagates the ambient tenant to outbound HTTP calls: sets <see cref="TenantPropagation.TenantHeaderName"/> and
/// re-establishes the ambient tenant for the duration of the request so background continuations keep it.
/// </summary>
public sealed class TenantPropagationHandler(ITenantAccessor accessor) : DelegatingHandler
{
    protected override async System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!accessor.TryGet(out var tenant) || string.IsNullOrEmpty(tenant.TenantId))
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        request.Headers.Remove(TenantPropagation.TenantHeaderName);
        request.Headers.TryAddWithoutValidation(
            TenantPropagation.TenantHeaderName,
            tenant.TenantId
        );

        using var scope = accessor.BeginScope(tenant);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

public static class TenancyServiceCollectionExtensions
{
    public static IServiceCollection AddLinKitTenancy(this IServiceCollection services)
    {
        services.TryAddSingleton<ITenantAccessor, TenantAccessor>();
        services.TryAddScoped<IMessageConsumeInterceptor, TenantConsumeInterceptor>();
        // Resolving ITenantContext without an active scope throws the explanatory Require() error instead of
        // handing out a mutable context that would silently apply to no tenant.
        services.TryAddScoped(sp => sp.GetRequiredService<ITenantAccessor>().Require());
        services.TryAddTransient<TenantPropagationHandler>();
        return services;
    }
}
