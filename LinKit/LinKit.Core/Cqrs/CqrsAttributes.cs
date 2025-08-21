using System;

namespace LinKit.Core.Cqrs;

/// <summary>
/// Explicitly marks a class as a CQRS request handler for discovery by the LinKit source generator.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class CqrsHandlerAttribute : Attribute { }

/// <summary>
/// Defines a "context" to register multiple handlers at once, useful for modular architectures.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = true)]
public sealed class CqrsContextAttribute : Attribute
{
    public Type[] HandlerTypes { get; }
    public CqrsContextAttribute(params Type[] handlerTypes) => HandlerTypes = handlerTypes;
}

/// <summary>
/// Marks a class as a pipeline behavior, specifying its target contract interface and execution order.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = true)]
public sealed class CqrsBehaviorAttribute : Attribute
{
    public Type TargetInterface { get; }
    public int Order { get; }
    public CqrsBehaviorAttribute(Type targetInterface, int order = 0)
    {
        TargetInterface = targetInterface;
        Order = order;
    }
}

/// <summary>
/// Applies specific, ad-hoc pipeline behaviors directly to a request class.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = true)]
public sealed class ApplyBehaviorAttribute : Attribute
{
    public Type[] BehaviorTypes { get; }
    public ApplyBehaviorAttribute(params Type[] behaviorTypes) => BehaviorTypes = behaviorTypes;
}