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

/// <summary>
/// The tenant resolved for the current unit of work: its identifier, a display name and a bag of per-tenant values that
/// outlive a single request when the context is propagated.
/// </summary>
public interface ITenantContext
{
    /// <summary>
    /// Gets or sets the tenant identifier written to tenant-scoped columns and headers.
    /// </summary>
    string? TenantId { get; set; }

    /// <summary>
    /// Gets or sets the human readable tenant name used for logs and diagnostics.
    /// </summary>
    string? TenantName { get; set; }

    /// <summary>
    /// Gets a thread-safe bag for values that belong to the tenant, such as cached settings or feature flags.
    /// </summary>
    IDictionary<string, object> Items { get; }
}

/// <summary>
/// Default <see cref="ITenantContext"/> backed by a concurrent dictionary for <see cref="Items"/>.
/// </summary>
public class TenantContext : ITenantContext
{
    /// <inheritdoc />
    public string? TenantId { get; set; }

    /// <inheritdoc />
    public string? TenantName { get; set; }

    /// <inheritdoc />
    public IDictionary<string, object> Items { get; } = new ConcurrentDictionary<string, object>();
}

/// <summary>
/// A resolved tenant that lives until it is disposed. Disposing restores whatever tenant was ambient before the
/// scope started (which is <see langword="null"/> at the outermost level), so a tenant can never outlive its work.
/// </summary>
public interface ITenantScope : IDisposable
{
    /// <summary>The tenant that is ambient for the lifetime of this scope.</summary>
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

    /// <inheritdoc />
    public ITenantContext? Current => _current.Value;

    /// <inheritdoc />
    public bool TryGet([NotNullWhen(true)] out ITenantContext? context)
    {
        context = _current.Value;
        return context is not null;
    }

    /// <inheritdoc />
    public ITenantContext Require() =>
        _current.Value
        ?? throw new InvalidOperationException(
            "No ambient tenant is available. Establish one with ITenantAccessor.BeginScope(tenantId) (or the "
                + "tenant scope middleware) before running tenant-scoped work, or use TryGet when a tenant is optional."
        );

    /// <inheritdoc />
    /// <exception cref="ArgumentException"><paramref name="tenantId"/> is <see langword="null"/>, empty or whitespace.</exception>
    public ITenantScope BeginScope(string tenantId, string? tenantName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        return BeginScope(new TenantContext { TenantId = tenantId, TenantName = tenantName });
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
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

/// <summary>
/// Moves the tenant identifier between services: as an HTTP header for synchronous calls and as a message header for
/// asynchronous ones.
/// </summary>
public static class TenantPropagation
{
    /// <summary>HTTP header used to carry the tenant id between LinKit services.</summary>
    public const string TenantHeaderName = "x-tenant-id";

    /// <summary>Broker header used to carry the tenant id. Shares the name declared by <see cref="MessageHeaderNames"/>.</summary>
    public const string TenantMessageHeader = MessageHeaderNames.TenantId;

    /// <summary>
    /// Writes <paramref name="tenantId"/> to the message headers, or removes the header when the value is empty so a
    /// previous tenant can never leak into a new message.
    /// </summary>
    /// <param name="headers">Headers of the outgoing message.</param>
    /// <param name="tenantId">Tenant to send, or <see langword="null"/>/empty to send none.</param>
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

    /// <summary>
    /// Reads the tenant identifier from the message headers.
    /// </summary>
    /// <param name="headers">Headers of the received message.</param>
    /// <returns>The tenant identifier, or <see langword="null"/> when the message carries none.</returns>
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
    /// <summary>
    /// Establishes the tenant carried by the message for the lifetime of the handler, including an empty tenant when the
    /// message carries none.
    /// </summary>
    /// <param name="scopedServices">Scope that resolves the handler.</param>
    /// <param name="headers">Headers of the message being consumed.</param>
    /// <returns>A scope that restores the previous tenant when disposed.</returns>
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
    /// <summary>
    /// Adds the tenant header to the outbound request and keeps the ambient tenant alive for the duration of the call.
    /// </summary>
    /// <param name="request">The request being sent.</param>
    /// <param name="cancellationToken">Token that cancels the call.</param>
    /// <returns>The response returned by the inner handler.</returns>
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

/// <summary>
/// Registration helpers for tenancy.
/// </summary>
public static class TenancyServiceCollectionExtensions
{
    /// <summary>
    /// Registers the ambient tenant accessor, the message consume interceptor that restores the tenant of an incoming
    /// message, the scoped <see cref="ITenantContext"/> resolved through <see cref="ITenantAccessor.Require"/> and the
    /// outbound HTTP handler. Registrations are idempotent.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <returns>The same collection, so calls can be chained.</returns>
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
