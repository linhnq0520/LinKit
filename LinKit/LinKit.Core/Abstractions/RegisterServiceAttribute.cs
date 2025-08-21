using System;

namespace LinKit.Core.Abstractions;

/// <summary>
/// Marks a class for automatic dependency injection registration by the LinKit source generator.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class RegisterServiceAttribute : Attribute
{
    /// <summary>
    /// The lifetime of the service (Transient, Scoped, or Singleton). Defaults to Transient.
    /// </summary>
    public Lifetime Lifetime { get; }

    /// <summary>
    /// The service type (typically an interface) to register the implementation against.
    /// If null, the generator will try to infer the first interface, or fall back to self-registration.
    /// </summary>
    public Type? ServiceType { get; }

    public RegisterServiceAttribute(Lifetime lifetime = Lifetime.Transient, Type? serviceType = null)
    {
        Lifetime = lifetime;
        ServiceType = serviceType;
    }
}