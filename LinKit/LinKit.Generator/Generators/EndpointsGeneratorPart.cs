using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Generic;
using System.Linq;
using System.Text;

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
internal record EndpointServiceInfo(string RegistrationCode);

internal static class EndpointsGeneratorPart
{
    private const string EndpointAttributeName = "LinKit.Core.Endpoints.ApiEndpointAttribute";
    private const string FromRouteAttributeName = "LinKit.Core.Endpoints.FromRouteAttribute";
    private const string FromQueryAttributeName = "LinKit.Core.Endpoints.FromQueryAttribute";
    private const string FromHeaderAttributeName = "LinKit.Core.Endpoints.FromHeaderAttribute";
    private const string ICommandInterfaceName = "LinKit.Core.Cqrs.ICommand";
    private const string IQueryInterfaceName = "LinKit.Core.Cqrs.IQuery";

    #region Pipeline Setup

    public static IncrementalValueProvider<IReadOnlyList<EndpointServiceInfo>> GetServices(IncrementalGeneratorInitializationContext context)
    {
        return context.SyntaxProvider.CreateSyntaxProvider((_, _) => true, (_, _) => true)
            .Collect()
            .Select((_, _) => (IReadOnlyList<EndpointServiceInfo>)new List<EndpointServiceInfo>());
    }

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
    }
    #endregion

    #region Data Collection (Không đổi)

    private static EndpointInfo? GetEndpointInfo(GeneratorAttributeSyntaxContext context)
    {
        if (context.TargetSymbol is not INamedTypeSymbol requestSymbol)
        {
            return null;
        }

        var attributeData = context.Attributes[0];
        var httpMethodEnum = (Core.Endpoints.ApiMethod)(attributeData.ConstructorArguments[0].Value ?? 0);
        var route = attributeData.ConstructorArguments[1].Value as string ?? "";
        var cqrsInterface = requestSymbol.AllInterfaces.FirstOrDefault(i => i.OriginalDefinition.ToDisplayString().StartsWith(IQueryInterfaceName) || i.OriginalDefinition.ToDisplayString().StartsWith(ICommandInterfaceName));
        if (cqrsInterface is null)
        {
            return null;
        }

        string responseType;
        bool isCommandWithoutResult = false;
        if (cqrsInterface.TypeArguments.Length > 0)
        {
            responseType = cqrsInterface.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
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
            if (prop.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == FromRouteAttributeName)) { parameters.Add(new ParameterInfo(prop.Name, prop.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), "Route")); }
            else if (prop.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == FromQueryAttributeName)) { parameters.Add(new ParameterInfo(prop.Name, prop.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), "Query")); }
            else if (prop.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == FromHeaderAttributeName)) { parameters.Add(new ParameterInfo(prop.Name, prop.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), "Header")); }
        }
        var httpMethodString = httpMethodEnum.ToString().ToUpper();
        if (httpMethodString == "POST" || httpMethodString == "PUT" || httpMethodString == "PATCH")
        {
            parameters.Add(new ParameterInfo("request", requestSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), "Body"));
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
    #endregion

    #region Source Generation
    private static string GenerateEndpointsExtension(IReadOnlyList<EndpointInfo> endpoints)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated> by LinKit.Generator");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using LinKit.Core.Cqrs;");
        sb.AppendLine("using LinKit.Core.Endpoints;");
        sb.AppendLine("using Microsoft.AspNetCore.Builder;");
        sb.AppendLine("using Microsoft.AspNetCore.Http;");
        sb.AppendLine("using Microsoft.AspNetCore.Routing;");
        sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");
        sb.AppendLine("using Microsoft.Extensions.Logging;");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.ComponentModel.DataAnnotations;");
        sb.AppendLine("using System.Threading;");
        sb.AppendLine();
        sb.AppendLine("namespace LinKit.Core");
        sb.AppendLine("{");
        sb.AppendLine("    public static partial class GeneratedEndpointsExtensions");
        sb.AppendLine("    {");
        sb.AppendLine("        public static IEndpointRouteBuilder MapGeneratedEndpoints(this IEndpointRouteBuilder app)");
        sb.AppendLine("        {");

        foreach (var endpoint in endpoints)
        {
            var handlerParams = new List<string>();
            var requestCreationParts = new List<string>();
            string requestInstanceName = "request";
            bool hasBody = endpoint.Parameters.Any(p => p.Source == "Body");

            handlerParams.Add("HttpContext context");

            foreach (var param in endpoint.Parameters.Where(p => p.Source != "Body"))
            {
                handlerParams.Add($"[Microsoft.AspNetCore.Mvc.From{param.Source}] {param.Type} {param.Name}");
            }

            if (hasBody)
            {
                var bodyParam = endpoint.Parameters.First(p => p.Source == "Body");
                handlerParams.Add($"[Microsoft.AspNetCore.Mvc.FromBody] {bodyParam.Type} {bodyParam.Name}");
            }

            handlerParams.Add("[Microsoft.AspNetCore.Mvc.FromServices] IMediator mediator");
            handlerParams.Add("CancellationToken ct");

            sb.AppendLine($@"            app.MapMethods(""{endpoint.Route}"", new[] {{ ""{endpoint.HttpMethod.ToUpper()}"", ""OPTIONS"" }}, async ({string.Join(", ", handlerParams)}) =>");
            sb.AppendLine("            {");
            sb.AppendLine("                try");
            sb.AppendLine("                {");

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
                sb.AppendLine(requestCreationParts.Any()
                    ? $"                    var {requestInstanceName} = new {endpoint.RequestType} {{ {string.Join(", ", requestCreationParts)} }};"
                    : $"                    var {requestInstanceName} = new {endpoint.RequestType}();");
            }

            if (endpoint.IsCommandWithoutResult)
            {
                sb.AppendLine($"                    await mediator.SendAsync({requestInstanceName}, ct);");
                sb.AppendLine("                    return Results.Ok();");
            }
            else
            {
                var isQuery = endpoint.HttpMethod.ToUpper() == "GET";
                var mediatorMethod = isQuery ? "QueryAsync" : "SendAsync";
                sb.AppendLine($"                    var result = await mediator.{mediatorMethod}<{endpoint.RequestType}, {endpoint.ResponseType.TrimEnd('?')}>({requestInstanceName}, ct);");
                sb.AppendLine("                    return result is not null ? Results.Ok(result) : Results.NotFound();");
            }

            sb.AppendLine("                }");
            sb.AppendLine("                catch (ValidationException ex)");
            sb.AppendLine("                {");
            sb.AppendLine("                    return Results.BadRequest(new { Error = ex.Message });");
            sb.AppendLine("                }");
            sb.AppendLine("                catch");
            sb.AppendLine("                {");
            sb.AppendLine("                    throw;");
            sb.AppendLine("                }");
            sb.AppendLine("            });");
        }

        sb.AppendLine("            return app;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }
    #endregion
}