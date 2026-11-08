using System;

namespace LinKit.Core.Endpoints;

/// <summary>
/// Binds a property or parameter from the route values of the request.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class FromRouteAttribute : Attribute { }

/// <summary>
/// Binds a property or parameter from the query string of the request.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class FromQueryAttribute : Attribute { }

/// <summary>
/// Binds a property or parameter from a request header.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class FromHeaderAttribute : Attribute { }

/// <summary>
/// Binds a property or parameter from the request body, which is deserialized with the endpoint JSON options.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class FromBodyAttribute : Attribute { }
