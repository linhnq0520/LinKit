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
    string? Policies,
    IReadOnlyList<string> Filters
);

internal record EndpointInfo(
    string RequestType,
    string ResponseType,
    string? CustomResponseType,
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
    string? Tag,
    string? MediatorKey,
    IReadOnlyList<string> Filters,
    int SuccessStatusCode,
    IReadOnlyList<int> ProducesStatusCodes,
    bool AsParameters,
    bool Deprecated,
    bool ExcludeFromDescription,
    string? OperationId
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
    private const string EndpointFilterAttributeName =
        "LinKit.Core.Endpoints.EndpointFilterAttribute";
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
                    List<string> assemblyFilters = [];

                    foreach (AttributeData attr in compilation.Assembly.GetAttributes())
                    {
                        if (attr.AttributeClass?.ToDisplayString() == EndpointFilterAttributeName)
                        {
                            if (attr.ConstructorArguments.Length > 0 && attr.ConstructorArguments[0].Value is ITypeSymbol filterSymbol)
                            {
                                assemblyFilters.Add(filterSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                            }
                        }
                    }

                    foreach (AttributeData attr in compilation.Assembly.GetAttributes())
                    {
                        if (attr.AttributeClass?.ToDisplayString() == RouteGroupAttributeName)
                        {
                            RouteGroupInfo? group = ExtractRouteGroupInfo(attr);
                            if (group != null)
                            {
                                if (assemblyFilters.Count > 0)
                                {
                                    var combinedFilters = new List<string>(group.Filters);
                                    foreach (var af in assemblyFilters)
                                    {
                                        if (!combinedFilters.Contains(af))
                                            combinedFilters.Add(af);
                                    }
                                    group = group with { Filters = combinedFilters };
                                }
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
        List<string> filters = [];

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
                case "Filters":
                    if (named.Value.Kind == TypedConstantKind.Array)
                    {
                        foreach (var item in named.Value.Values)
                        {
                            if (item.Value is ITypeSymbol filterSymbol)
                            {
                                filters.Add(filterSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                            }
                        }
                    }
                    break;
            }
        }
        return new RouteGroupInfo(prefix!, tag, feature, requireAuth, policies, filters);
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
            tag = null,
            mediatorKey = null,
            customResponseType = null,
            operationId = null;
        bool requireAuth = false,
            allowAnonymous = false,
            asParameters = false,
            deprecated = false,
            excludeFromDescription = false;
        int successStatusCode = 200;
        List<int> producesStatusCodes = [];
        List<(string FilterType, int Order)> orderedFilters = [];

        foreach (KeyValuePair<string, TypedConstant> named in attributeData.NamedArguments)
        {
            switch (named.Key)
            {
                case "Tag":
                    tag = named.Value.Value as string;
                    break;
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
                case "ResponseType":
                    if (named.Value.Value is ITypeSymbol resType)
                    {
                        customResponseType = resType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                    }
                    break;
                case "SuccessStatusCode":
                    if (named.Value.Value is int sc)
                    {
                        successStatusCode = sc;
                    }
                    break;
                case "ProducesStatusCodes":
                    if (named.Value.Kind == TypedConstantKind.Array)
                    {
                        foreach (var val in named.Value.Values)
                        {
                            if (val.Value is int pCode)
                                producesStatusCodes.Add(pCode);
                        }
                    }
                    break;
                case "AsParameters":
                    asParameters = (bool)(named.Value.Value ?? false);
                    break;
                case "Deprecated":
                    deprecated = (bool)(named.Value.Value ?? false);
                    break;
                case "ExcludeFromDescription":
                    excludeFromDescription = (bool)(named.Value.Value ?? false);
                    break;
                case "OperationId":
                    operationId = named.Value.Value as string;
                    break;
                case "Filters":
                    if (named.Value.Kind == TypedConstantKind.Array)
                    {
                        int idx = 0;
                        foreach (var val in named.Value.Values)
                        {
                            if (val.Value is ITypeSymbol fType)
                            {
                                orderedFilters.Add((fType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), idx++));
                            }
                        }
                    }
                    break;
            }
        }

        // Also collect [EndpointFilter(typeof(MyFilter), Order = ...)] attributes from requestSymbol
        foreach (var attr in requestSymbol.GetAttributes())
        {
            if (attr.AttributeClass?.ToDisplayString() == EndpointFilterAttributeName)
            {
                if (attr.ConstructorArguments.Length > 0 && attr.ConstructorArguments[0].Value is ITypeSymbol filterType)
                {
                    int order = 0;
                    if (attr.ConstructorArguments.Length > 1 && attr.ConstructorArguments[1].Value is int o)
                    {
                        order = o;
                    }
                    foreach (var named in attr.NamedArguments)
                    {
                        if (named.Key == "Order" && named.Value.Value is int ord)
                        {
                            order = ord;
                        }
                    }
                    orderedFilters.Add((filterType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), order));
                }
            }
        }

        var sortedFilters = orderedFilters
            .OrderBy(f => f.Order)
            .Select(f => f.FilterType)
            .Distinct()
            .ToList();

        return new EndpointInfo(
            RequestType: requestSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            ResponseType: responseType,
            CustomResponseType: customResponseType,
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
            Tag: tag,
            MediatorKey: mediatorKey,
            Filters: sortedFilters,
            SuccessStatusCode: successStatusCode,
            ProducesStatusCodes: producesStatusCodes,
            AsParameters: asParameters,
            Deprecated: deprecated,
            ExcludeFromDescription: excludeFromDescription,
            OperationId: operationId
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

        // Helper to handle Ok/NotFound safely for both ValueTypes and ReferenceTypes, with custom status codes and IResult support
        sb.AppendLine(
            "        private static IResult ToResult<T>(T result, int statusCode = 200)"
        );
        sb.AppendLine("        {");
        sb.AppendLine("            if (result is null) return Results.NotFound();");
        sb.AppendLine("            if (result is IResult r) return r;");
        sb.AppendLine("            return statusCode switch");
        sb.AppendLine("            {");
        sb.AppendLine("                200 => Results.Ok(result),");
        sb.AppendLine("                201 => Results.Json(result, statusCode: 201),");
        sb.AppendLine("                _ => Results.Json(result, statusCode: statusCode)");
        sb.AppendLine("            };");
        sb.AppendLine("        }");
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

                foreach (var filter in groupInfo.Filters)
                {
                    sb.AppendLine($"                .AddEndpointFilter<{filter}>()");
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
        bool useAsParameters = endpoint.AsParameters || !isBodyMethod;

        if (useAsParameters)
        {
            handlerParams.Add(
                $"[Microsoft.AspNetCore.Http.AsParameters] {endpoint.RequestType} request"
            );
        }
        else
        {
            handlerParams.Add(
                $"[Microsoft.AspNetCore.Mvc.FromBody] {endpoint.RequestType} request"
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
        string finalRoute;
        if (groupInfo != null)
        {
            string normPrefix = NormalizeRoute(groupInfo.Prefix);
            string routePath = endpoint.Route;
            if (routePath.StartsWith(normPrefix, StringComparison.OrdinalIgnoreCase))
            {
                routePath = routePath.Substring(normPrefix.Length);
            }
            finalRoute = routePath.TrimStart('/');
        }
        else
        {
            finalRoute = endpoint.Route;
        }

        sb.AppendLine(
            $"            {builder}.{mapMethod}(\"{finalRoute}\", async ({string.Join(", ", handlerParams)}) =>"
        );
        sb.AppendLine("            {");

        if (endpoint.IsCommandWithoutResult)
        {
            sb.AppendLine("                await mediator.SendAsync(request, cancellationToken);");
            string successReturn = endpoint.SuccessStatusCode switch
            {
                204 => "Results.NoContent();",
                201 => "Results.StatusCode(StatusCodes.Status201Created);",
                200 => "Results.Ok();",
                _ => $"Results.StatusCode({endpoint.SuccessStatusCode});",
            };
            sb.AppendLine($"                return {successReturn}");
        }
        else
        {
            string call = endpoint.IsCommand ? "SendAsync" : "QueryAsync";
            sb.AppendLine(
                $"                var result = await mediator.{call}(request, cancellationToken);"
            );
            // Use the ToResult helper to avoid boxing and handle ValueTypes correctly
            sb.AppendLine($"                return ToResult(result, {endpoint.SuccessStatusCode});");
        }

        sb.AppendLine("            })");

        // Filters
        foreach (var filter in endpoint.Filters)
        {
            sb.AppendLine($"            .AddEndpointFilter<{filter}>()");
        }

        // Metadata preservation
        string endpointName = endpoint.OperationId ?? endpoint.CustomName ?? GenerateEndpointName(endpoint);
        sb.AppendLine(
            $"            .WithName(\"{endpointName}\")"
        );
        string finalTag = !string.IsNullOrEmpty(endpoint.Tag)
            ? endpoint.Tag!
            : endpoint.FeatureName;
        sb.AppendLine($"            .WithTags(\"{finalTag}\")");

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

        if (endpoint.Deprecated)
        {
            sb.AppendLine("            .WithMetadata(new System.ObsoleteAttribute())");
        }

        if (endpoint.ExcludeFromDescription)
        {
            sb.AppendLine("            .ExcludeFromDescription()");
        }

        string producesType = endpoint.CustomResponseType ?? endpoint.ResponseType;

        if (endpoint.IsCommandWithoutResult)
        {
            sb.AppendLine($"            .Produces({endpoint.SuccessStatusCode})");
        }
        else
        {
            sb.AppendLine(
                $"            .Produces<{producesType}>({endpoint.SuccessStatusCode})"
            );
            sb.AppendLine("            .Produces(StatusCodes.Status404NotFound)");
        }

        foreach (int statusCode in endpoint.ProducesStatusCodes)
        {
            sb.AppendLine($"            .Produces({statusCode})");
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
