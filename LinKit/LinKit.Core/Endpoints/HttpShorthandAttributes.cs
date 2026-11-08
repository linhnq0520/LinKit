using System;

namespace LinKit.Core.Endpoints;

/// <summary>
/// Exposes a CQRS Query or Command as an HTTP GET endpoint.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class HttpGetAttribute : ApiEndpointAttribute
{
    /// <summary>
    /// Initializes the attribute.
    /// </summary>
    /// <param name="route">Route template of the endpoint.</param>
    public HttpGetAttribute(string route)
        : base(ApiMethod.Get, route) { }
}

/// <summary>
/// Exposes a CQRS Command as an HTTP POST endpoint.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class HttpPostAttribute : ApiEndpointAttribute
{
    /// <summary>
    /// Initializes the attribute.
    /// </summary>
    /// <param name="route">Route template of the endpoint.</param>
    public HttpPostAttribute(string route)
        : base(ApiMethod.Post, route) { }
}

/// <summary>
/// Exposes a CQRS Command as an HTTP PUT endpoint.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class HttpPutAttribute : ApiEndpointAttribute
{
    /// <summary>
    /// Initializes the attribute.
    /// </summary>
    /// <param name="route">Route template of the endpoint.</param>
    public HttpPutAttribute(string route)
        : base(ApiMethod.Put, route) { }
}

/// <summary>
/// Exposes a CQRS Command as an HTTP DELETE endpoint.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class HttpDeleteAttribute : ApiEndpointAttribute
{
    /// <summary>
    /// Initializes the attribute.
    /// </summary>
    /// <param name="route">Route template of the endpoint.</param>
    public HttpDeleteAttribute(string route)
        : base(ApiMethod.Delete, route) { }
}

/// <summary>
/// Exposes a CQRS Command as an HTTP PATCH endpoint.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class HttpPatchAttribute : ApiEndpointAttribute
{
    /// <summary>
    /// Initializes the attribute.
    /// </summary>
    /// <param name="route">Route template of the endpoint.</param>
    public HttpPatchAttribute(string route)
        : base(ApiMethod.Patch, route) { }
}
