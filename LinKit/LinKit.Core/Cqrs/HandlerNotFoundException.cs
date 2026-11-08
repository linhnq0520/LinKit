namespace LinKit.Core.Cqrs;

/// <summary>
/// Thrown when a request reaches the mediator but no handler is registered for its type, which usually means the source
/// generator did not see the handler or the containing assembly was not passed to <c>AddLinKitCqrs</c>.
/// </summary>
/// <param name="type">The request type that had no handler.</param>
public sealed class HandlerNotFoundException(Type type)
    : Exception($"No handler registered for type: {type.FullName}") { }
