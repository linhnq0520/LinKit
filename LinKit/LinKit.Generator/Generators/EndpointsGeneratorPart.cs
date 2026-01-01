using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace LinKit.Generator.Generators;

internal record RouteGroupInfo(
    string Prefix,
    string? Tag,
    string? Feature,
    bool RequireAuthorization,
    string? Policies
);

internal record EndpointInfo(
    string RequestType,
    string ResponseType,
    string HttpMethod,
    string Route,
    bool IsCommandWithoutResult,
    bool IsCommand,
    string FeatureName,
    string? CustomName,
    string? GroupPrefix,
    string? Policies,
    string? Roles,
    bool RequireAuthorization,
    bool AllowAnonymous,
    string? Summary,
    string? Description,
    string? RateLimitPolicy,
    string? CorsPolicy,
    string? Version,
    string? MediatorKey
);

internal record ExceptionMappingInfo(
    string ExceptionType,
    int StatusCode,
    string? Title,
    bool IncludeDetails,
    bool LogException,
    string LogLevel
);

internal static class EndpointsGeneratorPart
{
    private const string EndpointAttributeName = "LinKit.Core.Endpoints.ApiEndpointAttribute";
    private const string RouteGroupAttributeName = "LinKit.Core.Endpoints.ApiRouteGroupAttribute";
    private const string ExceptionMappingAttributeName =
        "LinKit.Core.Endpoints.ApiExceptionMappingAttribute";
    private const string ICommandInterfaceName = "LinKit.Core.Cqrs.ICommand";
    private const string IQueryInterfaceName = "LinKit.Core.Cqrs.IQuery";

    #region Pipeline Setup

    public static void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValueProvider<List<RouteGroupInfo>> routeGroups =
            context.CompilationProvider.Select(
                (compilation, _) =>
                {
                    List<RouteGroupInfo> groups = [];
                    foreach (AttributeData attr in compilation.Assembly.GetAttributes())
                    {
                        if (attr.AttributeClass?.ToDisplayString() == RouteGroupAttributeName)
                        {
                            RouteGroupInfo? group = ExtractRouteGroupInfo(attr);
                            if (group != null)
                            {
                                groups.Add(group);
                            }
                        }
                    }
                    return groups;
                }
            );

        IncrementalValuesProvider<EndpointInfo?> endpointDeclarations = context
            .SyntaxProvider.ForAttributeWithMetadataName(
                EndpointAttributeName,
                predicate: (node, _) => node is ClassDeclarationSyntax or RecordDeclarationSyntax,
                transform: (ctx, _) => GetEndpointInfo(ctx)
            )
            .Where(info => info is not null);

        IncrementalValuesProvider<ExceptionMappingInfo?> exceptionMappings = context
            .SyntaxProvider.ForAttributeWithMetadataName(
                ExceptionMappingAttributeName,
                predicate: (node, _) => node is ClassDeclarationSyntax,
                transform: (ctx, _) => GetExceptionMappingInfo(ctx)
            )
            .Where(info => info is not null);

        context.RegisterSourceOutput(
            endpointDeclarations.Collect().Combine(routeGroups),
            (spc, data) =>
            {
                var (endpoints, groups) = data;
                List<EndpointInfo> validEndpoints = endpoints.OfType<EndpointInfo>().ToList();
                if (!validEndpoints.Any())
                {
                    return;
                }

                string source = GenerateEndpointsExtension(validEndpoints, groups);
                spc.AddSource("Endpoints.g.cs", SourceText.From(source, Encoding.UTF8));
            }
        );

        context.RegisterSourceOutput(
            exceptionMappings.Collect(),
            (spc, mappings) =>
            {
                List<ExceptionMappingInfo> validMappings = mappings
                    .OfType<ExceptionMappingInfo>()
                    .ToList();
                string source = GenerateExceptionHandlerMiddleware(validMappings);
                spc.AddSource("ExceptionHandler.g.cs", SourceText.From(source, Encoding.UTF8));
            }
        );
    }

    #endregion

    #region Data Collection

    private static RouteGroupInfo? ExtractRouteGroupInfo(AttributeData attr)
    {
        if (attr.ConstructorArguments.Length == 0)
        {
            return null;
        }

        string? prefix = attr.ConstructorArguments[0].Value as string;
        if (string.IsNullOrEmpty(prefix))
        {
            return null;
        }

        string? tag = null,
            feature = null,
            policies = null;
        bool requireAuth = false;

        foreach (KeyValuePair<string, TypedConstant> named in attr.NamedArguments)
        {
            switch (named.Key)
            {
                case "Tag":
                    tag = named.Value.Value as string;
                    break;
                case "Feature":
                    feature = named.Value.Value as string;
                    break;
                case "RequireAuthorization":
                    requireAuth = (bool)(named.Value.Value ?? false);
                    break;
                case "Policies":
                    policies = named.Value.Value as string;
                    break;
            }
        }
        return new RouteGroupInfo(prefix!, tag, feature, requireAuth, policies);
    }

    private static EndpointInfo? GetEndpointInfo(GeneratorAttributeSyntaxContext context)
    {
        if (context.TargetSymbol is not INamedTypeSymbol requestSymbol)
        {
            return null;
        }

        AttributeData attributeData = context.Attributes[0];
        var methodValue = attributeData.ConstructorArguments[0].Value;
        string httpMethodStr = methodValue is int i
            ? ((LinKit.Core.Endpoints.ApiMethod)i).ToString()
            : "Get";
        string route = attributeData.ConstructorArguments[1].Value as string ?? "";

        route = NormalizeRoute(route);

        INamedTypeSymbol? cqrsInterface = requestSymbol.AllInterfaces.FirstOrDefault(i =>
            i.OriginalDefinition.ToDisplayString().StartsWith(IQueryInterfaceName)
            || i.OriginalDefinition.ToDisplayString().StartsWith(ICommandInterfaceName)
        );

        if (cqrsInterface is null)
        {
            return null;
        }

        string responseType;
        bool isCommandWithoutResult = false;
        bool isCommand = cqrsInterface
            .OriginalDefinition.ToDisplayString()
            .StartsWith(ICommandInterfaceName);

        if (cqrsInterface.TypeArguments.Length > 0)
        {
            responseType = cqrsInterface
                .TypeArguments[0]
                .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        }
        else
        {
            responseType = "System.ValueTuple";
            isCommandWithoutResult = true;
        }

        string featureName =
            requestSymbol.ContainingNamespace.ToDisplayString().Split('.').LastOrDefault()
            ?? "Default";

        string? customName = null,
            groupPrefix = null,
            policies = null,
            roles = null,
            summary = null,
            description = null,
            rateLimitPolicy = null,
            corsPolicy = null,
            version = null,
            mediatorKey = null;
        bool requireAuth = false,
            allowAnonymous = false;

        foreach (KeyValuePair<string, TypedConstant> named in attributeData.NamedArguments)
        {
            switch (named.Key)
            {
                case "Name":
                    customName = named.Value.Value as string;
                    break;
                case "Group":
                    groupPrefix = named.Value.Value as string;
                    break;
                case "Policies":
                    policies = named.Value.Value as string;
                    break;
                case "Roles":
                    roles = named.Value.Value as string;
                    break;
                case "RequireAuthorization":
                    requireAuth = (bool)(named.Value.Value ?? false);
                    break;
                case "AllowAnonymous":
                    allowAnonymous = (bool)(named.Value.Value ?? false);
                    break;
                case "Summary":
                    summary = named.Value.Value as string;
                    break;
                case "Description":
                    description = named.Value.Value as string;
                    break;
                case "RateLimitPolicy":
                    rateLimitPolicy = named.Value.Value as string;
                    break;
                case "CorsPolicy":
                    corsPolicy = named.Value.Value as string;
                    break;
                case "Version":
                    version = named.Value.Value as string;
                    break;
                case "MediatorKey":
                    mediatorKey = named.Value.Value as string;
                    break;
            }
        }

        return new EndpointInfo(
            RequestType: requestSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            ResponseType: responseType,
            HttpMethod: httpMethodStr,
            Route: route,
            IsCommandWithoutResult: isCommandWithoutResult,
            IsCommand: isCommand,
            FeatureName: featureName,
            CustomName: customName,
            GroupPrefix: groupPrefix,
            Policies: policies,
            Roles: roles,
            RequireAuthorization: requireAuth,
            AllowAnonymous: allowAnonymous,
            Summary: summary,
            Description: description,
            RateLimitPolicy: rateLimitPolicy,
            CorsPolicy: corsPolicy,
            Version: version,
            MediatorKey: mediatorKey
        );
    }

    private static ExceptionMappingInfo? GetExceptionMappingInfo(
        GeneratorAttributeSyntaxContext context
    )
    {
        if (context.TargetSymbol is not INamedTypeSymbol exceptionSymbol)
        {
            return null;
        }

        AttributeData attributeData = context.Attributes[0];
        int statusCode = (int)(attributeData.ConstructorArguments[0].Value ?? 500);

        string? title = null;
        bool includeDetails = true,
            logException = true;
        string logLevel = "Error";

        foreach (KeyValuePair<string, TypedConstant> named in attributeData.NamedArguments)
        {
            switch (named.Key)
            {
                case "Title":
                    title = named.Value.Value as string;
                    break;
                case "IncludeDetails":
                    includeDetails = (bool)(named.Value.Value ?? true);
                    break;
                case "LogException":
                    logException = (bool)(named.Value.Value ?? true);
                    break;
                case "LogLevel":
                    logLevel = named.Value.Value as string ?? "Error";
                    break;
            }
        }

        return new ExceptionMappingInfo(
            exceptionSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            statusCode,
            title,
            includeDetails,
            logException,
            logLevel
        );
    }

    #endregion

    #region Route Utilities

    private static string NormalizeRoute(string route)
    {
        if (string.IsNullOrWhiteSpace(route))
        {
            return "";
        }

        route = route.Trim();
        if (!route.StartsWith("/"))
        {
            route = "/" + route;
        }

        route = Regex.Replace(route, @"/+", "/");
        if (route.Length > 1 && route.EndsWith("/"))
        {
            route = route.TrimEnd('/');
        }

        if (!IsValidRoute(route))
        {
            throw new InvalidOperationException($"Invalid route: {route}");
        }

        return route;
    }

    private static bool IsValidRoute(string route)
    {
        string paramPattern = @"\{[a-zA-Z_][a-zA-Z0-9_]*(:.*?)?\}";
        string[] segments = route.Split('/');
        foreach (string segment in segments)
        {
            if (string.IsNullOrEmpty(segment))
            {
                continue;
            }

            if (segment.Contains("{") && !Regex.IsMatch(segment, $"^{paramPattern}$"))
            {
                return false;
            }
        }
        return true;
    }

    #endregion

    #region Source Generation

    private static string GenerateEndpointsExtension(
        IReadOnlyList<EndpointInfo> endpoints,
        IReadOnlyList<RouteGroupInfo> routeGroups
    )
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("// <auto-generated> by LinKit.Generator");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using LinKit.Core.Cqrs;");
        sb.AppendLine("using Microsoft.AspNetCore.Builder;");
        sb.AppendLine("using Microsoft.AspNetCore.Http;");
        sb.AppendLine("using Microsoft.AspNetCore.Routing;");
        sb.AppendLine("using Microsoft.Extensions.DependencyInjection;"); // Required for Keyed Services
        sb.AppendLine("using System.Threading;");
        sb.AppendLine();
        sb.AppendLine("namespace LinKit.Core");
        sb.AppendLine("{");
        sb.AppendLine("    internal static partial class GeneratedEndpointsExtensions");
        sb.AppendLine("    {");

        // Private helper to handle Ok/NotFound safely for both ValueTypes and ReferenceTypes
        sb.AppendLine(
            "        private static IResult ToResult<T>(T result) => result is not null ? Results.Ok(result) : Results.NotFound();"
        );
        sb.AppendLine();

        sb.AppendLine(
            "        public static IEndpointRouteBuilder MapGeneratedEndpoints(this IEndpointRouteBuilder app)"
        );
        sb.AppendLine("        {");

        var groupedEndpoints = endpoints.GroupBy(e => e.GroupPrefix ?? e.FeatureName).ToList();

        foreach (var group in groupedEndpoints)
        {
            string groupKey = group.Key;
            RouteGroupInfo? groupInfo = routeGroups.FirstOrDefault(g =>
                g.Feature == groupKey || g.Prefix == groupKey
            );

            if (groupInfo != null)
            {
                sb.AppendLine($"            // Group: {groupKey}");
                sb.AppendLine(
                    $"            var group_{SanitizeIdentifier(groupKey)} = app.MapGroup(\"{NormalizeRoute(groupInfo.Prefix)}\")"
                );
                if (!string.IsNullOrEmpty(groupInfo.Tag))
                {
                    sb.AppendLine($"                .WithTags(\"{groupInfo.Tag}\")");
                }

                if (groupInfo.RequireAuthorization)
                {
                    sb.AppendLine("                .RequireAuthorization()");
                }

                if (!string.IsNullOrEmpty(groupInfo.Policies))
                {
                    var policies = groupInfo.Policies!.Split(',').Select(p => $"\"{p.Trim()}\"");
                    sb.AppendLine(
                        $"                .RequireAuthorization({string.Join(", ", policies)})"
                    );
                }
                sb.AppendLine("                ;");
                sb.AppendLine();
            }

            foreach (var endpoint in group)
            {
                GenerateEndpoint(sb, endpoint, groupInfo, groupKey);
            }
            sb.AppendLine();
        }

        sb.AppendLine("            return app;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }

    private static void GenerateEndpoint(
        StringBuilder sb,
        EndpointInfo endpoint,
        RouteGroupInfo? groupInfo,
        string groupKey
    )
    {
        List<string> handlerParams = [];
        bool isBodyMethod = !new[] { "GET", "DELETE" }.Contains(endpoint.HttpMethod.ToUpper());

        if (isBodyMethod)
        {
            handlerParams.Add(
                $"[Microsoft.AspNetCore.Mvc.FromBody] {endpoint.RequestType} request"
            );
        }
        else
        {
            handlerParams.Add(
                $"[Microsoft.AspNetCore.Http.AsParameters] {endpoint.RequestType} request"
            );
        }

        // Mediator Injection (Check for Keyed Services)
        string mediatorAttr = string.IsNullOrEmpty(endpoint.MediatorKey)
            ? "[Microsoft.AspNetCore.Mvc.FromServices]"
            : $"[Microsoft.Extensions.DependencyInjection.FromKeyedServices(\"{endpoint.MediatorKey}\")]";

        handlerParams.Add($"{mediatorAttr} IMediator mediator");
        handlerParams.Add("CancellationToken cancellationToken");

        string mapMethod = $"Map{endpoint.HttpMethod}";
        string builder = groupInfo != null ? $"group_{SanitizeIdentifier(groupKey)}" : "app";
        string finalRoute = groupInfo != null ? endpoint.Route.TrimStart('/') : endpoint.Route;

        sb.AppendLine(
            $"            {builder}.{mapMethod}(\"{finalRoute}\", async ({string.Join(", ", handlerParams)}) =>"
        );
        sb.AppendLine("            {");

        if (endpoint.IsCommandWithoutResult)
        {
            sb.AppendLine("                await mediator.SendAsync(request, cancellationToken);");
            sb.AppendLine("                return Results.Ok();");
        }
        else
        {
            string call = endpoint.IsCommand ? "SendAsync" : "QueryAsync";
            sb.AppendLine(
                $"                var result = await mediator.{call}(request, cancellationToken);"
            );
            // Use the ToResult helper to avoid boxing and handle ValueTypes correctly
            sb.AppendLine("                return ToResult(result);");
        }

        sb.AppendLine("            })");

        // Metadata preservation
        sb.AppendLine(
            $"            .WithName(\"{endpoint.CustomName ?? GenerateEndpointName(endpoint)}\")"
        );
        sb.AppendLine($"            .WithTags(\"{endpoint.FeatureName}\")");

        if (!string.IsNullOrEmpty(endpoint.Summary))
        {
            sb.AppendLine($"            .WithSummary(\"{EscapeString(endpoint.Summary!)}\")");
        }

        if (!string.IsNullOrEmpty(endpoint.Description))
        {
            sb.AppendLine(
                $"            .WithDescription(\"{EscapeString(endpoint.Description!)}\")"
            );
        }

        if (endpoint.AllowAnonymous)
        {
            sb.AppendLine("            .AllowAnonymous()");
        }
        else if (
            endpoint.RequireAuthorization
            || !string.IsNullOrEmpty(endpoint.Policies)
            || !string.IsNullOrEmpty(endpoint.Roles)
        )
        {
            if (!string.IsNullOrEmpty(endpoint.Roles))
            {
                sb.AppendLine(
                    $"            .RequireAuthorization(new Microsoft.AspNetCore.Authorization.AuthorizeAttribute {{ Roles = \"{endpoint.Roles}\" }})"
                );
            }
            else if (!string.IsNullOrEmpty(endpoint.Policies))
            {
                sb.AppendLine(
                    $"            .RequireAuthorization({string.Join(", ", endpoint.Policies!.Split(',').Select(p => $"\"{p.Trim()}\""))})"
                );
            }
            else
            {
                sb.AppendLine("            .RequireAuthorization()");
            }
        }

        if (!string.IsNullOrEmpty(endpoint.RateLimitPolicy))
        {
            sb.AppendLine($"            .RequireRateLimiting(\"{endpoint.RateLimitPolicy}\")");
        }

        if (!string.IsNullOrEmpty(endpoint.CorsPolicy))
        {
            sb.AppendLine($"            .RequireCors(\"{endpoint.CorsPolicy}\")");
        }

        if (endpoint.IsCommandWithoutResult)
        {
            sb.AppendLine("            .Produces(StatusCodes.Status200OK)");
        }
        else
        {
            sb.AppendLine(
                $"            .Produces<{endpoint.ResponseType}>(StatusCodes.Status200OK)"
            );
            sb.AppendLine("            .Produces(StatusCodes.Status404NotFound)");
        }
        sb.AppendLine(
            "            .ProducesValidationProblem().ProducesProblem(StatusCodes.Status500InternalServerError);"
        );
    }

    private static string GenerateEndpointName(EndpointInfo endpoint)
    {
        string requestTypeName = endpoint
            .RequestType.Split('.')
            .Last()
            .Replace("Query", "")
            .Replace("Command", "")
            .Replace("Request", "");
        return $"{endpoint.HttpMethod}{requestTypeName}";
    }

    private static string GenerateExceptionHandlerMiddleware(
        IReadOnlyList<ExceptionMappingInfo> mappings
    )
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("// <auto-generated> by LinKit.Generator");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using Microsoft.AspNetCore.Builder;");
        sb.AppendLine("using Microsoft.AspNetCore.Http;");
        sb.AppendLine("using Microsoft.AspNetCore.Diagnostics;");
        sb.AppendLine("using Microsoft.Extensions.Logging;");
        sb.AppendLine("using Microsoft.Extensions.Hosting;");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.ComponentModel.DataAnnotations;");
        sb.AppendLine("using System.Net;");
        sb.AppendLine("using System.Threading.Tasks;");
        sb.AppendLine();
        sb.AppendLine("namespace LinKit.Core");
        sb.AppendLine("{");
        sb.AppendLine("    internal static class GeneratedExceptionHandlerExtensions");
        sb.AppendLine("    {");
        sb.AppendLine(
            "        public static IApplicationBuilder UseGeneratedExceptionHandler(this IApplicationBuilder app)"
        );
        sb.AppendLine("        {");
        sb.AppendLine("            app.UseExceptionHandler(appError =>");
        sb.AppendLine("            {");
        sb.AppendLine("                appError.Run(async context =>");
        sb.AppendLine("                {");
        sb.AppendLine(
            "                    var contextFeature = context.Features.Get<IExceptionHandlerFeature>();"
        );
        sb.AppendLine("                    if (contextFeature == null) return;");
        sb.AppendLine(
            "                    var logger = context.RequestServices.GetService(typeof(ILogger<object>)) as ILogger;"
        );
        sb.AppendLine(
            "                    var env = context.RequestServices.GetService(typeof(IHostEnvironment)) as IHostEnvironment;"
        );
        sb.AppendLine("                    var isDevelopment = env?.IsDevelopment() ?? false;");
        sb.AppendLine("                    var exception = contextFeature.Error;");
        sb.AppendLine(
            "                    context.Response.ContentType = \"application/problem+json\";"
        );
        sb.AppendLine("                    switch (exception)");
        sb.AppendLine("                    {");

        // Built-in handlers
        AddBuiltInException(
            sb,
            "ValidationException",
            "HttpStatusCode.BadRequest",
            "Validation Error",
            400,
            "ex.Message"
        );
        AddBuiltInException(
            sb,
            "UnauthorizedAccessException",
            "HttpStatusCode.Forbidden",
            "Forbidden",
            403,
            "\"You do not have permission to access this resource.\""
        );
        AddBuiltInException(
            sb,
            "ArgumentException",
            "HttpStatusCode.BadRequest",
            "Bad Request",
            400,
            "ex.Message"
        );

        foreach (var mapping in mappings)
        {
            sb.AppendLine($"                        case {mapping.ExceptionType} ex:");
            if (mapping.LogException)
            {
                string logMethod = mapping.LogLevel switch
                {
                    "Warning" => "LogWarning",
                    "Information" => "LogInformation",
                    "Debug" => "LogDebug",
                    "Critical" => "LogCritical",
                    _ => "LogError",
                };
                sb.AppendLine(
                    $"                            logger?.{logMethod}(ex, \"Exception occurred\");"
                );
            }
            sb.AppendLine(
                $"                            context.Response.StatusCode = {mapping.StatusCode};"
            );
            sb.AppendLine(
                "                            await context.Response.WriteAsJsonAsync(new {"
            );
            sb.AppendLine(
                $"                                Type = \"https://tools.ietf.org/html/rfc7231\", Title = \"{mapping.Title ?? "Error"}\", Status = {mapping.StatusCode},"
            );
            sb.AppendLine(
                mapping.IncludeDetails
                    ? "                                Detail = isDevelopment ? ex.Message : \"An error occurred.\","
                    : "                                Detail = \"An error occurred.\","
            );
            sb.AppendLine("                                Instance = context.Request.Path");
            sb.AppendLine("                            }); break;");
        }

        sb.AppendLine("                        default:");
        sb.AppendLine(
            "                            logger?.LogError(exception, \"Unhandled exception occurred\");"
        );
        sb.AppendLine(
            "                            context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;"
        );
        sb.AppendLine(
            "                            await context.Response.WriteAsJsonAsync(new { Type = \"https://tools.ietf.org/html/rfc7231#section-6.6.1\", Title = \"Internal Server Error\", Status = 500, Detail = isDevelopment ? exception.Message : \"An unexpected error occurred.\", Instance = context.Request.Path, StackTrace = isDevelopment ? exception.StackTrace : null }); break;"
        );
        sb.AppendLine("                    }");
        sb.AppendLine("                });");
        sb.AppendLine("            }); return app;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static void AddBuiltInException(
        StringBuilder sb,
        string type,
        string statusEnum,
        string title,
        int code,
        string detail
    )
    {
        sb.AppendLine($"                        case {type} ex:");
        sb.AppendLine($"                            logger?.LogWarning(ex, \"{title} occurred\");");
        sb.AppendLine(
            $"                            context.Response.StatusCode = (int){statusEnum};"
        );
        sb.AppendLine(
            $"                            await context.Response.WriteAsJsonAsync(new {{ Type = \"https://tools.ietf.org/html/rfc7231\", Title = \"{title}\", Status = {code}, Detail = {detail}, Instance = context.Request.Path }}); break;"
        );
    }

    private static string SanitizeIdentifier(string input) =>
        Regex.Replace(input, @"[^a-zA-Z0-9_]", "_");

    private static string EscapeString(string input) =>
        input.Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");

    #endregion
}
