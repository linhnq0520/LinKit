using System.Collections.Generic;
using System.Linq;
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
    bool IsCommandWithoutResult
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
                    return;

                var source = GenerateEndpointsExtension(validEndpoints);
                spc.AddSource("Endpoints.g.cs", SourceText.From(source, Encoding.UTF8));
            }
        );
    }

    private static EndpointInfo? GetEndpointInfo(GeneratorAttributeSyntaxContext context)
    {
        if (context.TargetSymbol is not INamedTypeSymbol requestSymbol)
            return null;

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
            return null;

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
                    "request",
                    requestSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    "Body"
                )
            );
        }

        return new EndpointInfo(
            RequestType: requestSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            ResponseType: responseType,
            HttpMethod: httpMethodEnum.ToString(),
            Route: route,
            Parameters: parameters,
            IsCommandWithoutResult: isCommandWithoutResult
        );
    }

    private static string GenerateEndpointsExtension(IReadOnlyList<EndpointInfo> endpoints)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            @"// Auto-generated by LinKit.Generator
#nullable enable
using LinKit.Core.Cqrs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Mvc;
using System.Threading;

namespace LinKit.Core
{
    public static class GeneratedEndpointsExtensions
    {
        public static IEndpointRouteBuilder MapGeneratedEndpoints(this IEndpointRouteBuilder app, string groupPrefix = ""/api"")
        {
            var group = app.MapGroup(groupPrefix);
"
        );

        foreach (var endpoint in endpoints)
        {
            var methodName = $"Map{endpoint.HttpMethod}";
            var handlerParams = new List<string>();
            var requestCreationParts = new List<string>();
            string requestInstanceName = "request";

            bool hasBody = endpoint.Parameters.Any(p => p.Source == "Body");

            foreach (var param in endpoint.Parameters)
            {
                handlerParams.Add($"[From{param.Source}] {param.Type} {param.Name}");
            }
            handlerParams.Add("[FromServices] IMediator mediator");
            handlerParams.Add("CancellationToken ct");

            sb.Append(
                $@"
            group.{methodName}(""{endpoint.Route}"", async ({string.Join(", ", handlerParams)}) =>
            {{"
            );

            if (hasBody)
            {
                requestInstanceName = "request";
            }
            else
            {
                requestInstanceName = "finalRequest";
                foreach (var param in endpoint.Parameters)
                {
                    requestCreationParts.Add($"{param.Name} = {param.Name}");
                }

                if (requestCreationParts.Any())
                {
                    sb.Append(
                        $@"
                var {requestInstanceName} = new {endpoint.RequestType} {{ {string.Join(", ", requestCreationParts)} }};"
                    );
                }
                else
                {
                    sb.Append(
                        $@"
                var {requestInstanceName} = new {endpoint.RequestType}();"
                    );
                }
            }

            if (endpoint.IsCommandWithoutResult)
            {
                sb.Append(
                    $@"
                await mediator.SendAsync({requestInstanceName}, ct);
                return Results.Ok();"
                );
            }
            else
            {
                var isQuery = endpoint.HttpMethod.ToUpper() == "GET";
                var mediatorMethod = isQuery ? "QueryAsync" : "SendAsync";
                sb.Append(
                    $@"
                var result = await mediator.{mediatorMethod}({requestInstanceName}, ct);
                return result is not null ? Results.Ok(result) : Results.NotFound();"
                );
            }

            sb.AppendLine(
                @"
            });"
            );
        }

        sb.AppendLine(
            @"
            return app;
        }
    }
}
"
        );
        return sb.ToString();
    }
}
