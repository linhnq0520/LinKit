using System;

namespace LinKit.Core.Abstractions;

/// <summary>
/// Shorthand for <c>[RegisterService(Lifetime.Transient)]</c>.
/// Usage: <c>[AddTransient]</c>, <c>[AddTransient(typeof(IFoo))]</c>, <c>[AddTransient(Key = "a")]</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = true)]
public sealed class AddTransientAttribute : Attribute
{
    /// <summary>The service type to register against. If null, the first leaf interface or the class itself is used.</summary>
    public Type? ServiceType { get; }

    /// <summary>Optional key for keyed registration.</summary>
    public string? Key { get; set; }

    /// <summary>True to register an open generic type (e.g. IRepository&lt;&gt;).</summary>
    public bool IsGeneric { get; set; }

    public AddTransientAttribute(Type? serviceType = null) => ServiceType = serviceType;
}

/// <summary>
/// Shorthand for <c>[RegisterService(Lifetime.Scoped)]</c>.
/// Usage: <c>[AddScoped]</c>, <c>[AddScoped(typeof(IFoo))]</c>, <c>[AddScoped(Key = "a")]</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = true)]
public sealed class AddScopedAttribute : Attribute
{
    /// <summary>The service type to register against. If null, the first leaf interface or the class itself is used.</summary>
    public Type? ServiceType { get; }

    /// <summary>Optional key for keyed registration.</summary>
    public string? Key { get; set; }

    /// <summary>True to register an open generic type (e.g. IRepository&lt;&gt;).</summary>
    public bool IsGeneric { get; set; }

    public AddScopedAttribute(Type? serviceType = null) => ServiceType = serviceType;
}

/// <summary>
/// Shorthand for <c>[RegisterService(Lifetime.Singleton)]</c>.
/// Usage: <c>[AddSingleton]</c>, <c>[AddSingleton(typeof(IFoo))]</c>, <c>[AddSingleton(Key = "a")]</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = true)]
public sealed class AddSingletonAttribute : Attribute
{
    /// <summary>The service type to register against. If null, the first leaf interface or the class itself is used.</summary>
    public Type? ServiceType { get; }

    /// <summary>Optional key for keyed registration.</summary>
    public string? Key { get; set; }

    /// <summary>True to register an open generic type (e.g. IRepository&lt;&gt;).</summary>
    public bool IsGeneric { get; set; }

    public AddSingletonAttribute(Type? serviceType = null) => ServiceType = serviceType;
}
