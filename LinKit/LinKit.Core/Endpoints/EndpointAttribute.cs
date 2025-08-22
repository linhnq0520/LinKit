using System;

namespace LinKit.Core.Endpoints;

/// <summary>
/// Specifies the HTTP method for an endpoint.
/// </summary>
public enum ApiMethod
{
    Get,
    Post,
    Put,
    Delete,
    Patch,
}

/// <summary>
/// Marks a Command or Query class to be exposed as a Minimal API endpoint.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class ApiEndpointAttribute : Attribute
{
    /// <summary>
    /// The HTTP method for this endpoint (e.g., GET, POST).
    /// </summary>
    public ApiMethod Method { get; }

    /// <summary>
    /// The route template (e.g., "users/{id}").
    /// </summary>
    public string Route { get; }

    public ApiEndpointAttribute(ApiMethod method, string route)
    {
        Method = method;
        Route = route;
    }
}
