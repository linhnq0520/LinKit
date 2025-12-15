using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

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
    bool ICommand,
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
    string? Version
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
                (
                    System.Collections.Immutable.ImmutableArray<EndpointInfo> endpoints,
                    List<RouteGroupInfo> groups
                ) = data;
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
        Core.Endpoints.ApiMethod httpMethodEnum = (Core.Endpoints.ApiMethod)(
            attributeData.ConstructorArguments[0].Value ?? 0
        );
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
            roles = null;
        string? summary = null,
            description = null,
            rateLimitPolicy = null,
            corsPolicy = null,
            version = null;
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
            }
        }

        return new EndpointInfo(
            RequestType: requestSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            ResponseType: responseType,
            HttpMethod: httpMethodEnum.ToString(),
            Route: route,
            IsCommandWithoutResult: isCommandWithoutResult,
            ICommand: isCommand,
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
            Version: version
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
            ExceptionType: exceptionSymbol.ToDisplayString(
                SymbolDisplayFormat.FullyQualifiedFormat
            ),
            StatusCode: statusCode,
            Title: title,
            IncludeDetails: includeDetails,
            LogException: logException,
            LogLevel: logLevel
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

        // Remove leading/trailing whitespace
        route = route.Trim();

        // Ensure starts with /
        if (!route.StartsWith("/"))
        {
            route = "/" + route;
        }

        // Remove duplicate slashes
        route = Regex.Replace(route, @"/+", "/");

        // Remove trailing slash (except for root)
        if (route.Length > 1 && route.EndsWith("/"))
        {
            route = route.TrimEnd('/');
        }

        // Validate route parameters
        if (!IsValidRoute(route))
        {
            throw new InvalidOperationException($"Invalid route: {route}");
        }

        return route;
    }

    private static bool IsValidRoute(string route)
    {
        // Check for valid route parameter syntax
        string paramPattern = @"\{[a-zA-Z_][a-zA-Z0-9_]*(:.*?)?\}";
        string[] segments = route.Split('/');

        foreach (string? segment in segments)
        {
            if (string.IsNullOrEmpty(segment))
            {
                continue;
            }

            if (segment.Contains("{"))
            {
                if (!Regex.IsMatch(segment, $"^{paramPattern}$"))
                {
                    return false;
                }
            }
        }

        return true;
    }

    //private static string CombineRoutes(string? prefix, string route)
    //{
    //    if (string.IsNullOrEmpty(prefix))
    //    {
    //        return route;
    //    }

    //    prefix = NormalizeRoute(prefix);
    //    route = route.TrimStart('/');

    //    return $"{prefix}/{route}";
    //}

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
        sb.AppendLine("using System.Threading;");
        sb.AppendLine();
        sb.AppendLine("namespace LinKit.Core");
        sb.AppendLine("{");
        sb.AppendLine("    internal static partial class GeneratedEndpointsExtensions");
        sb.AppendLine("    {");
        sb.AppendLine(
            "        public static IEndpointRouteBuilder MapGeneratedEndpoints(this IEndpointRouteBuilder app)"
        );
        sb.AppendLine("        {");

        // Group endpoints by feature/group
        List<IGrouping<string, EndpointInfo>> groupedEndpoints = endpoints
            .GroupBy(e => e.GroupPrefix ?? e.FeatureName)
            .ToList();

        foreach (IGrouping<string, EndpointInfo>? group in groupedEndpoints)
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
                    IEnumerable<string> policies = groupInfo
                        .Policies!.Split(',')
                        .Select(p => $"\"{p.Trim()}\"");
                    sb.AppendLine(
                        $"                .RequireAuthorization({string.Join(", ", policies)})"
                    );
                }

                sb.AppendLine("                ;");
                sb.AppendLine();
            }

            foreach (EndpointInfo? endpoint in group)
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
        bool isBodyMethod =
            endpoint.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase)
            || endpoint.HttpMethod.Equals("PUT", StringComparison.OrdinalIgnoreCase)
            || endpoint.HttpMethod.Equals("PATCH", StringComparison.OrdinalIgnoreCase);

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

        handlerParams.Add("[Microsoft.AspNetCore.Mvc.FromServices] IMediator mediator");
        handlerParams.Add("CancellationToken cancellationToken");

        string mapMethod = endpoint.HttpMethod switch
        {
            "Get" => "MapGet",
            "Post" => "MapPost",
            "Put" => "MapPut",
            "Delete" => "MapDelete",
            "Patch" => "MapPatch",
            _ =>
                $"MapMethods(\"{endpoint.Route}\", new[] {{ \"{endpoint.HttpMethod.ToUpper()}\" }})",
        };

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
            if (endpoint.ICommand)
            {
                sb.AppendLine(
                    $"                var result = await mediator.SendAsync(request, cancellationToken);"
                );
            }
            else
            {
                sb.AppendLine(
                    $"                var result = await mediator.QueryAsync(request, cancellationToken);"
                );
            }
            sb.AppendLine(
                "                return result is not null ? Results.Ok(result) : Results.NotFound();"
            );
        }

        sb.AppendLine("            })");

        // Endpoint name
        string endpointName = endpoint.CustomName ?? GenerateEndpointName(endpoint);
        sb.AppendLine($"            .WithName(\"{endpointName}\")");

        // Tags
        sb.AppendLine($"            .WithTags(\"{endpoint.FeatureName}\")");

        // Summary & Description
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

        // Authorization
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
            List<string> authPolicies = [];

            if (!string.IsNullOrEmpty(endpoint.Policies))
            {
                authPolicies.AddRange(endpoint.Policies!.Split(',').Select(p => $"\"{p.Trim()}\""));
            }

            if (!string.IsNullOrEmpty(endpoint.Roles))
            {
                IEnumerable<string> roles = endpoint
                    .Roles!.Split(',')
                    .Select(r => $"\"{r.Trim()}\"");
                sb.AppendLine(
                    $"            .RequireAuthorization(new Microsoft.AspNetCore.Authorization.AuthorizeAttribute {{ Roles = \"{endpoint.Roles}\" }})"
                );
            }
            else if (authPolicies.Any())
            {
                sb.AppendLine(
                    $"            .RequireAuthorization({string.Join(", ", authPolicies)})"
                );
            }
            else
            {
                sb.AppendLine("            .RequireAuthorization()");
            }
        }

        // Rate limiting
        if (!string.IsNullOrEmpty(endpoint.RateLimitPolicy))
        {
            sb.AppendLine($"            .RequireRateLimiting(\"{endpoint.RateLimitPolicy}\")");
        }

        // CORS
        if (!string.IsNullOrEmpty(endpoint.CorsPolicy))
        {
            sb.AppendLine($"            .RequireCors(\"{endpoint.CorsPolicy}\")");
        }

        // Response types
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

        sb.AppendLine("            .ProducesValidationProblem()");
        sb.AppendLine("            .ProducesProblem(StatusCodes.Status500InternalServerError);");
        sb.AppendLine();
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
        sb.AppendLine();
        sb.AppendLine(
            "                    var logger = context.RequestServices.GetService(typeof(ILogger<object>)) as ILogger;"
        );
        sb.AppendLine(
            "                    var env = context.RequestServices.GetService(typeof(IHostEnvironment)) as IHostEnvironment;"
        );
        sb.AppendLine("                    var isDevelopment = env?.IsDevelopment() ?? false;");
        sb.AppendLine("                    var exception = contextFeature.Error;");
        sb.AppendLine();
        sb.AppendLine(
            "                    context.Response.ContentType = \"application/problem+json\";"
        );
        sb.AppendLine();
        sb.AppendLine("                    switch (exception)");
        sb.AppendLine("                    {");

        // Built-in exception handling
        sb.AppendLine("                        case ValidationException ex:");
        sb.AppendLine(
            "                            logger?.LogWarning(ex, \"Validation error occurred\");"
        );
        sb.AppendLine(
            "                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;"
        );
        sb.AppendLine("                            await context.Response.WriteAsJsonAsync(new");
        sb.AppendLine("                            {");
        sb.AppendLine(
            "                                Type = \"https://tools.ietf.org/html/rfc7231#section-6.5.1\","
        );
        sb.AppendLine("                                Title = \"Validation Error\",");
        sb.AppendLine("                                Status = 400,");
        sb.AppendLine("                                Detail = ex.Message,");
        sb.AppendLine("                                Instance = context.Request.Path");
        sb.AppendLine("                            });");
        sb.AppendLine("                            break;");
        sb.AppendLine();
        sb.AppendLine("                        case UnauthorizedAccessException ex:");
        sb.AppendLine(
            "                            logger?.LogWarning(ex, \"Unauthorized access attempt\");"
        );
        sb.AppendLine(
            "                            context.Response.StatusCode = (int)HttpStatusCode.Forbidden;"
        );
        sb.AppendLine("                            await context.Response.WriteAsJsonAsync(new");
        sb.AppendLine("                            {");
        sb.AppendLine(
            "                                Type = \"https://tools.ietf.org/html/rfc7231#section-6.5.3\","
        );
        sb.AppendLine("                                Title = \"Forbidden\",");
        sb.AppendLine("                                Status = 403,");
        sb.AppendLine(
            "                                Detail = \"You do not have permission to access this resource.\","
        );
        sb.AppendLine("                                Instance = context.Request.Path");
        sb.AppendLine("                            });");
        sb.AppendLine("                            break;");
        sb.AppendLine();
        sb.AppendLine("                        case ArgumentException ex:");
        sb.AppendLine(
            "                            logger?.LogWarning(ex, \"Invalid argument provided\");"
        );
        sb.AppendLine(
            "                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;"
        );
        sb.AppendLine("                            await context.Response.WriteAsJsonAsync(new");
        sb.AppendLine("                            {");
        sb.AppendLine(
            "                                Type = \"https://tools.ietf.org/html/rfc7231#section-6.5.1\","
        );
        sb.AppendLine("                                Title = \"Bad Request\",");
        sb.AppendLine("                                Status = 400,");
        sb.AppendLine("                                Detail = ex.Message,");
        sb.AppendLine("                                Instance = context.Request.Path");
        sb.AppendLine("                            });");
        sb.AppendLine("                            break;");
        sb.AppendLine();

        // Custom exception mappings
        foreach (ExceptionMappingInfo mapping in mappings)
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
                "                            await context.Response.WriteAsJsonAsync(new"
            );
            sb.AppendLine("                            {");
            sb.AppendLine(
                $"                                Type = \"https://tools.ietf.org/html/rfc7231\","
            );
            sb.AppendLine(
                $"                                Title = \"{mapping.Title ?? "Error"}\","
            );
            sb.AppendLine($"                                Status = {mapping.StatusCode},");

            if (mapping.IncludeDetails)
            {
                sb.AppendLine(
                    "                                Detail = isDevelopment ? ex.Message : \"An error occurred.\","
                );
            }
            else
            {
                sb.AppendLine("                                Detail = \"An error occurred.\",");
            }

            sb.AppendLine("                                Instance = context.Request.Path");
            sb.AppendLine("                            });");
            sb.AppendLine("                            break;");
            sb.AppendLine();
        }

        // Default case
        sb.AppendLine("                        default:");
        sb.AppendLine(
            "                            logger?.LogError(exception, \"Unhandled exception occurred\");"
        );
        sb.AppendLine(
            "                            context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;"
        );
        sb.AppendLine("                            await context.Response.WriteAsJsonAsync(new");
        sb.AppendLine("                            {");
        sb.AppendLine(
            "                                Type = \"https://tools.ietf.org/html/rfc7231#section-6.6.1\","
        );
        sb.AppendLine("                                Title = \"Internal Server Error\",");
        sb.AppendLine("                                Status = 500,");
        sb.AppendLine(
            "                                Detail = isDevelopment ? exception.Message : \"An unexpected error occurred. Please try again later.\","
        );
        sb.AppendLine("                                Instance = context.Request.Path,");
        sb.AppendLine(
            "                                StackTrace = isDevelopment ? exception.StackTrace : null"
        );
        sb.AppendLine("                            });");
        sb.AppendLine("                            break;");
        sb.AppendLine("                    }");
        sb.AppendLine("                });");
        sb.AppendLine("            });");
        sb.AppendLine();
        sb.AppendLine("            return app;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }

    private static string SanitizeIdentifier(string input)
    {
        return Regex.Replace(input, @"[^a-zA-Z0-9_]", "_");
    }

    private static string EscapeString(string input)
    {
        return input.Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
    }

    #endregion
}
