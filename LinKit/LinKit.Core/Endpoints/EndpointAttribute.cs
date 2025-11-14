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

[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class ApiEndpointAttribute : Attribute
{
    public ApiMethod Method { get; }

    public string Route { get; }

    public ApiEndpointAttribute(ApiMethod method, string route)
    {
        Method = method;
        Route = route;
    }
}
