using System;

namespace LinKit.Core.Abstractions;

/// <summary>
/// Marks a class for automatic dependency injection registration by the LinKit source generator.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = true)]
public sealed class RegisterServiceAttribute : Attribute
{
    /// <summary>
    /// The lifetime of the service (Transient, Scoped, or Singleton). Defaults to Transient.
    /// </summary>
    public Lifetime Lifetime { get; set; }

    /// <summary>
    /// The service type (typically an interface) to register the implementation against.
    /// If null, the generator will try to infer the first interface, or fall back to self-registration.
    /// </summary>
    public Type? ServiceType { get; set; }

    /// <summary>
    /// The key for keyed service registration (optional, used in .NET 8+).
    /// </summary>
    public string? Key { get; set; }

    /// <summary>
    /// Indicates if the service is a generic type (for example <c>IRepository&lt;&gt;</c>).
    /// </summary>
    public bool IsGeneric { get; set; }

    /// <summary>
    /// Initializes the attribute.
    /// </summary>
    /// <param name="lifetime">Service lifetime; defaults to <see cref="Lifetime.Transient"/>.</param>
    /// <param name="serviceType">Service type to register against; <see langword="null"/> lets the generator infer it.</param>
    /// <param name="key">Key for keyed registration, or <see langword="null"/> for an unkeyed registration.</param>
    /// <param name="isGeneric">Whether the registration is for an open generic service type.</param>
    public RegisterServiceAttribute(
        Lifetime lifetime = Lifetime.Transient,
        Type? serviceType = null,
        string? key = null,
        bool isGeneric = false
    )
    {
        Lifetime = lifetime;
        ServiceType = serviceType;
        Key = key;
        IsGeneric = isGeneric;
    }
}
