using System;

namespace LinKit.Core.Caching;

/// <summary>
/// Declares cache-aside metadata for a CQRS query.
/// This attribute is currently metadata only; it does not add generated or runtime behavior.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class CacheAsideAttribute : Attribute
{
    public string KeyTemplate { get; }
    public int TtlSeconds { get; set; } = 300;
    public int SlidingExpirationSeconds { get; set; } = 0;

    public CacheAsideAttribute(string keyTemplate, int ttlSeconds = 300)
    {
        KeyTemplate = keyTemplate;
        TtlSeconds = ttlSeconds;
    }
}

/// <summary>
/// Declares cache invalidation metadata for a CQRS command.
/// This attribute is currently metadata only; it does not add generated or runtime behavior.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class InvalidateCacheAttribute : Attribute
{
    public string[] KeyTemplates { get; }

    public InvalidateCacheAttribute(params string[] keyTemplates)
    {
        KeyTemplates = keyTemplates;
    }
}
