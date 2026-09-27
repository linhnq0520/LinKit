using System;

namespace LinKit.Core.Endpoints;

public enum ApiMethod
{
    Get,
    Post,
    Put,
    Delete,
    Patch,
}

/// <summary>
/// Defines an API endpoint with routing, naming, and authorization metadata
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class ApiEndpointAttribute : Attribute
{
    /// <summary>
    /// HTTP method for the endpoint
    /// </summary>
    public ApiMethod Method { get; }

    /// <summary>
    /// Route template (will be prefixed with group if specified)
    /// </summary>
    public string Route { get; }

    /// <summary>
    /// Optional custom endpoint name. If not specified, generated from request type
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Optional route group prefix (e.g., "api/v1/users")
    /// If not specified, inferred from namespace
    /// </summary>
    public string? Group { get; set; }

    /// <summary>
    /// Comma-separated list of required policies (e.g., "Admin,Manager")
    /// </summary>
    public string? Policies { get; set; }

    /// <summary>
    /// Comma-separated list of required roles (e.g., "Admin,User")
    /// </summary>
    public string? Roles { get; set; }

    /// <summary>
    /// Whether this endpoint requires authentication (default: false)
    /// </summary>
    public bool RequireAuthorization { get; set; }

    /// <summary>
    /// Whether this endpoint allows anonymous access (overrides RequireAuthorization)
    /// </summary>
    public bool AllowAnonymous { get; set; }

    /// <summary>
    /// Custom summary for OpenAPI documentation
    /// </summary>
    public string? Summary { get; set; }

    /// <summary>
    /// Custom description for OpenAPI documentation
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Rate limiting policy name
    /// </summary>
    public string? RateLimitPolicy { get; set; }

    /// <summary>
    /// CORS policy name
    /// </summary>
    public string? CorsPolicy { get; set; }

    /// <summary>
    /// API version (e.g., "1.0", "2.0")
    /// </summary>
    public string? Version { get; set; }
    public string? MediatorKey { get; set; }

    /// <summary>
    /// Optional tag name for OpenAPI grouping
    /// </summary>
    public string? Tag { get; set; }

    /// <summary>
    /// Custom response type for OpenAPI/Swagger documentation.
    /// If not specified, inferred from CQRS response type.
    /// </summary>
    public Type? ResponseType { get; set; }

    /// <summary>
    /// List of endpoint filter types (must implement Microsoft.AspNetCore.Http.IEndpointFilter).
    /// </summary>
    public Type[]? Filters { get; set; }

    /// <summary>
    /// Success HTTP status code (default: 200 OK). E.g., 201 for Created, 204 for NoContent.
    /// </summary>
    public int SuccessStatusCode { get; set; } = 200;

    /// <summary>
    /// Additional HTTP status codes produced by this endpoint for OpenAPI documentation.
    /// E.g. [StatusCodes.Status400BadRequest, StatusCodes.Status409Conflict]
    /// </summary>
    public int[]? ProducesStatusCodes { get; set; }

    /// <summary>
    /// Whether to bind request parameters using [AsParameters] instead of [FromBody].
    /// Useful for POST/PUT requests with mixed route, query, or header parameters.
    /// </summary>
    public bool AsParameters { get; set; }

    /// <summary>
    /// Marks the endpoint as deprecated in OpenAPI / Swagger documentation.
    /// </summary>
    public bool Deprecated { get; set; }

    /// <summary>
    /// Exclude this endpoint from OpenAPI / Swagger documentation.
    /// </summary>
    public bool ExcludeFromDescription { get; set; }

    /// <summary>
    /// Custom OperationId for OpenAPI documentation.
    /// </summary>
    public string? OperationId { get; set; }

    public ApiEndpointAttribute(ApiMethod method, string route)
    {
        Method = method;
        Route = route;
    }
}

/// <summary>
/// Applies an endpoint filter (IEndpointFilter) to the endpoint or route group.
/// Can be specified multiple times; filters execute in order.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Assembly, AllowMultiple = true, Inherited = false)]
public sealed class EndpointFilterAttribute : Attribute
{
    /// <summary>
    /// The type of filter, which must implement Microsoft.AspNetCore.Http.IEndpointFilter
    /// </summary>
    public Type FilterType { get; }

    /// <summary>
    /// Execution order of the filter (lower numbers execute first)
    /// </summary>
    public int Order { get; set; }

    public EndpointFilterAttribute(Type filterType, int order = 0)
    {
        FilterType = filterType;
        Order = order;
    }
}

/// <summary>
/// Configures global route grouping for all endpoints in a feature
/// Apply to assembly or namespace level
/// </summary>
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class, AllowMultiple = true)]
public sealed class ApiRouteGroupAttribute : Attribute
{
    /// <summary>
    /// Route prefix for the group (e.g., "api/v1/users")
    /// </summary>
    public string Prefix { get; }

    /// <summary>
    /// Optional tag name for OpenAPI grouping
    /// </summary>
    public string? Tag { get; set; }

    /// <summary>
    /// Feature/module name this group applies to
    /// </summary>
    public string? Feature { get; set; }

    /// <summary>
    /// Whether to require authorization for all endpoints in this group
    /// Individual endpoints can override with AllowAnonymous
    /// </summary>
    public bool RequireAuthorization { get; set; }

    /// <summary>
    /// Default policies for all endpoints in this group
    /// </summary>
    public string? Policies { get; set; }

    /// <summary>
    /// List of endpoint filter types applied to all endpoints in this group.
    /// </summary>
    public Type[]? Filters { get; set; }

    public ApiRouteGroupAttribute(string prefix)
    {
        Prefix = prefix;
    }
}

/// <summary>
/// Marks an exception type for custom handling in the exception middleware
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
public sealed class ApiExceptionMappingAttribute : Attribute
{
    /// <summary>
    /// HTTP status code to return for this exception
    /// </summary>
    public int StatusCode { get; }

    /// <summary>
    /// Problem title for the response
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    /// Whether to include exception message in response (default: true in dev, false in prod)
    /// </summary>
    public bool IncludeDetails { get; set; } = true;

    /// <summary>
    /// Whether to log this exception (default: true)
    /// </summary>
    public bool LogException { get; set; } = true;

    /// <summary>
    /// Log level (default: Error)
    /// </summary>
    public string LogLevel { get; set; } = "Error";

    public ApiExceptionMappingAttribute(int statusCode)
    {
        StatusCode = statusCode;
    }
}
