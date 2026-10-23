using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using LinKit.Core.Messaging;
using Microsoft.Extensions.DependencyInjection;

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

public interface ITenantAccessor
{
    ITenantContext Context { get; }
}

public class TenantAccessor : ITenantAccessor
{
    private static readonly AsyncLocal<ITenantContext?> _current = new();

    public ITenantContext Context
    {
        get => _current.Value ??= new TenantContext();
        set => _current.Value = value;
    }
}

public static class TenantPropagation
{
    public const string TenantHeaderName = "x-tenant-id";
    public const string TenantMessageHeader = "linkit-tenant-id";

    public static void Inject(MessageHeaders headers, string? tenantId)
    {
        if (!string.IsNullOrEmpty(tenantId))
        {
            headers[TenantMessageHeader] = tenantId;
        }
    }

    public static string? Extract(MessageHeaders headers)
    {
        if (headers.TryGetValue(TenantMessageHeader, out var val))
        {
            return val?.ToString();
        }
        return null;
    }
}

/// <summary>
/// Restores the tenant ID from the message header for the duration of one consumed message.
/// The header is trusted as broker-internal metadata; do not accept it from untrusted publishers.
/// </summary>
public sealed class TenantConsumeInterceptor(ITenantAccessor accessor) : IMessageConsumeInterceptor
{
    public IDisposable? Enter(IServiceProvider scopedServices, IReadOnlyDictionary<string, object> headers)
    {
        var tenantId = TenantPropagation.Extract(new MessageHeaders(headers));
        if (string.IsNullOrEmpty(tenantId) || accessor is not TenantAccessor tenantAccessor) return null;

        var previous = tenantAccessor.Context;
        tenantAccessor.Context = new TenantContext { TenantId = tenantId };
        return new Restore(tenantAccessor, previous);
    }

    private sealed class Restore(TenantAccessor accessor, ITenantContext previous) : IDisposable
    {
        public void Dispose() => accessor.Context = previous;
    }
}

public static class TenancyServiceCollectionExtensions
{
    public static IServiceCollection AddLinKitTenancy(this IServiceCollection services)
    {
        services.AddSingleton<ITenantAccessor, TenantAccessor>();
        services.AddScoped<IMessageConsumeInterceptor, TenantConsumeInterceptor>();
        services.AddScoped(sp => sp.GetRequiredService<ITenantAccessor>().Context);
        return services;
    }
}
