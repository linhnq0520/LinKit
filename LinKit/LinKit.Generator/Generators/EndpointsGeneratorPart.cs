using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace LinKit.Generator.Generators;

internal record EndpointInfo(
    string RequestType,
    string ResponseType,
    string HttpMethod,
    string Route,
    IReadOnlyList<ParameterInfo> Parameters,
    bool IsCommandWithoutResult,
    string FeatureName
);

internal record ParameterInfo(string Name, string Type, string Source);

internal static class EndpointsGeneratorPart
{
    private const string EndpointAttributeName = "LinKit.Core.Endpoints.ApiEndpointAttribute";
    private const string FromRouteAttributeName = "LinKit.Core.Endpoints.FromRouteAttribute";
    private const string FromQueryAttributeName = "LinKit.Core.Endpoints.FromQueryAttribute";
    private const string FromHeaderAttributeName = "LinKit.Core.Endpoints.FromHeaderAttribute";
    private const string ICommandInterfaceName = "LinKit.Core.Cqrs.ICommand";
    private const string IQueryInterfaceName = "LinKit.Core.Cqrs.IQuery";

    #region Pipeline Setup

    public static void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<EndpointInfo?> endpointDeclarations = context
            .SyntaxProvider.ForAttributeWithMetadataName(
                EndpointAttributeName,
                predicate: (node, _) => node is ClassDeclarationSyntax or RecordDeclarationSyntax,
                transform: (ctx, _) => GetEndpointInfo(ctx)
            )
            .Where(info => info is not null);

        context.RegisterSourceOutput(
            endpointDeclarations.Collect(),
            (spc, endpoints) =>
            {
                var validEndpoints = endpoints.OfType<EndpointInfo>().ToList();
                if (!validEndpoints.Any())
                {
                    return;
                }

                var source = GenerateEndpointsExtension(validEndpoints);
                spc.AddSource("Endpoints.g.cs", SourceText.From(source, Encoding.UTF8));
            }
        );

        context.RegisterSourceOutput(
            context.CompilationProvider,
            (spc, _) =>
            {
                var source = GenerateExceptionHandlerMiddleware();
                spc.AddSource("ExceptionHandler.g.cs", SourceText.From(source, Encoding.UTF8));
            }
        );
    }
    #endregion

    #region Data Collection

    private static EndpointInfo? GetEndpointInfo(GeneratorAttributeSyntaxContext context)
    {
        if (context.TargetSymbol is not INamedTypeSymbol requestSymbol)
        {
            return null;
        }

        var attributeData = context.Attributes[0];
        var httpMethodEnum = (Core.Endpoints.ApiMethod)(
            attributeData.ConstructorArguments[0].Value ?? 0
        );
        var route = attributeData.ConstructorArguments[1].Value as string ?? "";
        var cqrsInterface = requestSymbol.AllInterfaces.FirstOrDefault(i =>
            i.OriginalDefinition.ToDisplayString().StartsWith(IQueryInterfaceName)
            || i.OriginalDefinition.ToDisplayString().StartsWith(ICommandInterfaceName)
        );
        if (cqrsInterface is null)
        {
            return null;
        }

        string responseType;
        bool isCommandWithoutResult = false;
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

        var parameters = new List<ParameterInfo>();
        var allProperties = requestSymbol.GetMembers().OfType<IPropertySymbol>().ToList();
        foreach (var prop in allProperties.Where(p => p.SetMethod is not null))
        {
            if (
                prop.GetAttributes()
                    .Any(a => a.AttributeClass?.ToDisplayString() == FromRouteAttributeName)
            )
            {
                parameters.Add(
                    new ParameterInfo(
                        prop.Name,
                        prop.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                        "Route"
                    )
                );
            }
            else if (
                prop.GetAttributes()
                    .Any(a => a.AttributeClass?.ToDisplayString() == FromQueryAttributeName)
            )
            {
                parameters.Add(
                    new ParameterInfo(
                        prop.Name,
                        prop.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                        "Query"
                    )
                );
            }
            else if (
                prop.GetAttributes()
                    .Any(a => a.AttributeClass?.ToDisplayString() == FromHeaderAttributeName)
            )
            {
                parameters.Add(
                    new ParameterInfo(
                        prop.Name,
                        prop.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                        "Header"
                    )
                );
            }
        }

        var httpMethodString = httpMethodEnum.ToString().ToUpper();
        if (httpMethodString == "POST" || httpMethodString == "PUT" || httpMethodString == "PATCH")
        {
            parameters.Add(
                new ParameterInfo(
                    "requestBody",
                    requestSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    "Body"
                )
            );
        }

        var featureName =
            requestSymbol.ContainingNamespace.ToDisplayString().Split('.').LastOrDefault()
            ?? "Default";

        return new EndpointInfo(
            RequestType: requestSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            ResponseType: responseType,
            HttpMethod: httpMethodEnum.ToString(),
            Route: route,
            Parameters: parameters,
            IsCommandWithoutResult: isCommandWithoutResult,
            FeatureName: featureName
        );
    }
    #endregion

    #region Source Generation (Refactored)

    private static string GenerateExceptionHandlerMiddleware()
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated> by LinKit.Generator");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using Microsoft.AspNetCore.Builder;");
        sb.AppendLine("using Microsoft.AspNetCore.Http;");
        sb.AppendLine("using Microsoft.AspNetCore.Diagnostics;");
        sb.AppendLine("using Microsoft.Extensions.Logging;");
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
        sb.AppendLine("                    if (contextFeature != null)");
        sb.AppendLine("                    {");
        sb.AppendLine(
            "                        var logger = context.RequestServices.GetService(typeof(ILogger<object>)) as ILogger;"
        );
        sb.AppendLine(
            "                        context.Response.ContentType = \"application/problem+json\";"
        );
        sb.AppendLine();
        sb.AppendLine("                        switch (contextFeature.Error)");
        sb.AppendLine("                        {");
        sb.AppendLine("                            case ValidationException ex:");
        sb.AppendLine(
            "                                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;"
        );
        sb.AppendLine(
            "                                await context.Response.WriteAsJsonAsync(new { Title = \"Validation Error\", Detail = ex.Message, Status = 400 });"
        );
        sb.AppendLine("                                break;");
        sb.AppendLine("                            default:");
        sb.AppendLine(
            "                                logger?.LogError($\"Something went wrong: {contextFeature.Error}\");"
        );
        sb.AppendLine(
            "                                context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;"
        );
        sb.AppendLine(
            "                                await context.Response.WriteAsJsonAsync(new { Title = \"An unexpected error occurred.\", Detail = \"Please try again later.\", Status = 500 });"
        );
        sb.AppendLine("                                break;");
        sb.AppendLine("                        }");
        sb.AppendLine("                    }");
        sb.AppendLine("                });");
        sb.AppendLine("            });");
        sb.AppendLine("            return app;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string GenerateEndpointsExtension(IReadOnlyList<EndpointInfo> endpoints)
    {
        var sb = new StringBuilder();
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

        foreach (var endpoint in endpoints)
        {
            var handlerParams = new List<string>();
            var requestCreationParts = new List<string>();
            var hasBody = endpoint.Parameters.Any(p => p.Source == "Body");

            foreach (var param in endpoint.Parameters.Where(p => p.Source != "Body"))
            {
                handlerParams.Add(
                    $"[Microsoft.AspNetCore.Mvc.From{param.Source}] {param.Type} {param.Name}"
                );
                requestCreationParts.Add($"{param.Name} = {param.Name}");
            }

            if (hasBody)
            {
                var bodyParam = endpoint.Parameters.First(p => p.Source == "Body");
                handlerParams.Add(
                    $"[Microsoft.AspNetCore.Mvc.FromBody] {bodyParam.Type} {bodyParam.Name}"
                );
            }

            handlerParams.Add("[Microsoft.AspNetCore.Mvc.FromServices] IMediator mediator");
            handlerParams.Add("CancellationToken cancellationToken");

            var mapMethod = endpoint.HttpMethod switch
            {
                "Get" => "MapGet",
                "Post" => "MapPost",
                "Put" => "MapPut",
                "Delete" => "MapDelete",
                "Patch" => "MapPatch",
                _ =>
                    $"MapMethods(\"{endpoint.Route}\", new[] {{ \"{endpoint.HttpMethod.ToUpper()}\" }})",
            };

            sb.AppendLine(
                $"            app.{mapMethod}(\"{endpoint.Route}\", async ({string.Join(", ", handlerParams)}) =>"
            );
            sb.AppendLine("            {");

            if (hasBody)
            {
                sb.AppendLine($"                var request = requestBody;");
            }
            else
            {
                sb.AppendLine(
                    requestCreationParts.Any()
                        ? $"                var request = new {endpoint.RequestType} {{ {string.Join(", ", requestCreationParts)} }};"
                        : $"                var request = new {endpoint.RequestType}();"
                );
            }

            if (endpoint.IsCommandWithoutResult)
            {
                sb.AppendLine(
                    "                await mediator.SendAsync(request, cancellationToken);"
                );
                sb.AppendLine("                return Results.Ok();");
            }
            else
            {
                sb.AppendLine(
                    $"                var result = await mediator.QueryAsync<{endpoint.ResponseType.TrimEnd('?')}> (request, cancellationToken);"
                );
                sb.AppendLine(
                    "                return result is not null ? Results.Ok(result) : Results.NotFound();"
                );
            }

            sb.AppendLine("            })");

            var endpointName =
                $"{endpoint.HttpMethod}{endpoint.RequestType.Split('.').Last()}{GetDeterministicHashCode(endpoint.Route)}";
            sb.AppendLine($"            .WithName(\"{endpointName}\")");
            sb.AppendLine($"            .WithTags(\"{endpoint.FeatureName}\")");

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
            sb.AppendLine(
                "            .ProducesProblem(StatusCodes.Status500InternalServerError);"
            );
            sb.AppendLine();
        }

        sb.AppendLine("            return app;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }

    private static string GetDeterministicHashCode(string str)
    {
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(str));
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant().Substring(0, 8);
    }
    #endregion
}
