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
    /// <summary>
    /// Gets the types whose assemblies are scanned for handlers when this context is registered.
    /// </summary>
    public Type[] AssemblyMarkerTypes { get; }

    /// <summary>
    /// Initializes the attribute.
    /// </summary>
    /// <param name="assemblyMarkerTypes">
    /// One type per assembly to scan. A single marker per module is enough; the whole assembly is scanned.
    /// </param>
    public CqrsContextAttribute(params Type[] assemblyMarkerTypes) =>
        AssemblyMarkerTypes = assemblyMarkerTypes;
}

/// <summary>
/// Marks a class as a pipeline behavior, specifying its target contract interface and execution order.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = true)]
public sealed class CqrsBehaviorAttribute : Attribute
{
    /// <summary>
    /// Gets the open generic interface the behavior is registered against, for example
    /// <c>typeof(IPipelineBehavior&lt;,&gt;)</c>.
    /// </summary>
    public Type TargetInterface { get; }

    /// <summary>
    /// Gets or sets the position of the behavior in the pipeline. Lower values run first (outermost).
    /// </summary>
    public int Order { get; set; }

    /// <summary>
    /// Initializes the attribute.
    /// </summary>
    /// <param name="targetInterface">Open generic interface implemented by the behavior.</param>
    /// <param name="order">Position in the pipeline; lower values run first.</param>
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
    /// <summary>
    /// Gets the behavior types applied to the annotated request, in the order they were declared.
    /// </summary>
    public Type[] BehaviorTypes { get; }

    /// <summary>
    /// Initializes the attribute.
    /// </summary>
    /// <param name="behaviorTypes">Behavior types to run around this request.</param>
    public ApplyBehaviorAttribute(params Type[] behaviorTypes) => BehaviorTypes = behaviorTypes;
}
