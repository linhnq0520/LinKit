namespace LinKit.Core.Abstractions;

/// <summary>
/// Service lifetime used by <see cref="RegisterServiceAttribute"/> and its shorthand attributes. The values map one to one
/// onto the Microsoft DI lifetimes.
/// </summary>
public enum Lifetime
{
    /// <summary>
    /// A new instance is created for every resolution. Use for stateless, cheap services.
    /// </summary>
    Transient,

    /// <summary>
    /// One instance per scope, typically one HTTP request. Use for services that hold per-request state such as a
    /// DbContext or a unit of work.
    /// </summary>
    Scoped,

    /// <summary>
    /// One instance for the whole application. Use for thread-safe, long-lived services such as caches or clients.
    /// </summary>
    Singleton,
}
