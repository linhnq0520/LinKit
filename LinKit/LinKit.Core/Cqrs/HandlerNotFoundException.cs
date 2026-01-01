namespace LinKit.Core.Cqrs;

public sealed class HandlerNotFoundException(Type type)
    : Exception($"No handler registered for type: {type.FullName}") { }
