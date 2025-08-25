using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace LinKit.Generator.Generators;

internal record GrpcEndpointInfo(
    string CqrsRequestType,
    string CqrsResponseType,
    string ServiceBaseType,
    string GrpcMethodName,
    string GrpcRequestType,
    string GrpcResponseType,
    IReadOnlyList<PropertyMap> RequestPropertyMaps,
    IReadOnlyList<PropertyMap> ResponsePropertyMaps,
    IReadOnlyList<ListPropertyMap> ResponseListPropertyMaps,
    string? GrpcResponseDtoProperty,
    string? GrpcResponseDtoType,
    bool IsCqrsQuery
);

internal record PropertyMap(string SourceProperty, string DestProperty);

internal record ListPropertyMap(
    string SourceProperty,
    string DestProperty,
    string SourceItemType,
    string DestItemType,
    IReadOnlyList<PropertyMap> ItemPropertyMaps
);

internal static class GrpcGeneratorPart
{
    private const string GrpcEndpointAttributeName = "LinKit.Core.Grpc.GrpcEndpointAttribute";
    private const string IQueryInterfaceName = "LinKit.Core.Cqrs.IQuery";

    public static void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<GrpcEndpointInfo?> grpcDeclarations = context
            .SyntaxProvider.ForAttributeWithMetadataName(
                GrpcEndpointAttributeName,
                predicate: (node, _) => node is ClassDeclarationSyntax or RecordDeclarationSyntax,
                transform: (ctx, _) => GetGrpcEndpointInfo(ctx)
            )
            .Where(info => info is not null);

        context.RegisterSourceOutput(
            grpcDeclarations.Collect(),
            (spc, endpoints) =>
            {
                var validEndpoints = endpoints.OfType<GrpcEndpointInfo>().ToList();
                if (!validEndpoints.Any())
                    return;

                var source = GenerateGrpcServices(validEndpoints);
                spc.AddSource("Grpc.Services.g.cs", SourceText.From(source, Encoding.UTF8));
            }
        );
    }

    private static GrpcEndpointInfo? GetGrpcEndpointInfo(GeneratorAttributeSyntaxContext context)
    {
        if (context.TargetSymbol is not INamedTypeSymbol cqrsRequestSymbol)
            return null;
        var attributeData = context.Attributes[0];
        if (
            attributeData.ConstructorArguments.Length < 2
            || attributeData.ConstructorArguments[0].Value is not INamedTypeSymbol serviceBaseSymbol
            || attributeData.ConstructorArguments[1].Value is not string methodName
        )
        {
            return null;
        }

        var rpcMethod = serviceBaseSymbol
            .GetMembers(methodName)
            .OfType<IMethodSymbol>()
            .FirstOrDefault(m => m.Parameters.Length == 2 && m.Name == methodName && !m.IsStatic);
        if (rpcMethod is null)
            return null;

        var grpcRequestSymbol = rpcMethod.Parameters[0].Type as INamedTypeSymbol;
        var grpcResponseSymbol =
            (rpcMethod.ReturnType as INamedTypeSymbol)?.TypeArguments.FirstOrDefault()
            as INamedTypeSymbol;
        if (grpcRequestSymbol is null || grpcResponseSymbol is null)
            return null;

        var cqrsInterface = cqrsRequestSymbol.AllInterfaces.FirstOrDefault(i =>
            i.ToDisplayString().Contains("LinKit.Core.Cqrs.I")
        );
        if (cqrsInterface is null || cqrsInterface.TypeArguments.Length == 0)
            return null;
        var cqrsResponseSymbol = cqrsInterface.TypeArguments[0] as INamedTypeSymbol;
        if (cqrsResponseSymbol is null)
            return null;

        var requestMaps = GetPropertyMaps(grpcRequestSymbol, cqrsRequestSymbol);
        var (responseMaps, responseListMaps) = GetResponseMaps(
            cqrsResponseSymbol,
            grpcResponseSymbol
        );
        var grpcResponseDtoType = grpcResponseSymbol.ToDisplayString(
            SymbolDisplayFormat.FullyQualifiedFormat
        );

        bool isQuery = cqrsInterface
            .OriginalDefinition.ToDisplayString()
            .StartsWith(IQueryInterfaceName);

        return new GrpcEndpointInfo(
            CqrsRequestType: cqrsRequestSymbol.ToDisplayString(
                SymbolDisplayFormat.FullyQualifiedFormat
            ),
            CqrsResponseType: cqrsResponseSymbol.ToDisplayString(
                SymbolDisplayFormat.FullyQualifiedFormat
            ),
            ServiceBaseType: serviceBaseSymbol.ToDisplayString(
                SymbolDisplayFormat.FullyQualifiedFormat
            ),
            GrpcMethodName: methodName,
            GrpcRequestType: grpcRequestSymbol.ToDisplayString(
                SymbolDisplayFormat.FullyQualifiedFormat
            ),
            GrpcResponseType: grpcResponseSymbol.ToDisplayString(
                SymbolDisplayFormat.FullyQualifiedFormat
            ),
            RequestPropertyMaps: requestMaps,
            ResponsePropertyMaps: responseMaps,
            ResponseListPropertyMaps: responseListMaps,
            GrpcResponseDtoProperty: string.Empty,
            GrpcResponseDtoType: grpcResponseDtoType,
            IsCqrsQuery: isQuery
        );
    }

    private static (List<PropertyMap>, List<ListPropertyMap>) GetResponseMaps(
        INamedTypeSymbol? source,
        INamedTypeSymbol? destination
    )
    {
        if (source is null || destination is null)
            return (new List<PropertyMap>(), new List<ListPropertyMap>());

        var sourceProps = source
            .GetMembers()
            .OfType<IPropertySymbol>()
            .ToDictionary(p => p.Name, p => p, StringComparer.OrdinalIgnoreCase);

        var destProps = destination
            .GetMembers()
            .OfType<IPropertySymbol>()
            .Where(p => p.SetMethod is not null || IsRepeatedType(p.Type))
            .ToList();

        var simpleMaps = new List<PropertyMap>();
        var listMaps = new List<ListPropertyMap>();

        foreach (var destProp in destProps)
        {
            if (sourceProps.TryGetValue(destProp.Name, out var sourceProp))
            {
                bool isSourceList = IsListType(sourceProp.Type);
                bool isDestList = IsRepeatedType(destProp.Type);

                if (isSourceList && isDestList)
                {
                    var sourceItemType = GetListItemType(sourceProp.Type);
                    var destItemType = GetRepeatedItemType(destProp.Type);
                    if (sourceItemType != null && destItemType != null)
                    {
                        var itemMaps = GetPropertyMaps(sourceItemType, destItemType);
                        listMaps.Add(
                            new ListPropertyMap(
                                SourceProperty: sourceProp.Name,
                                DestProperty: destProp.Name,
                                SourceItemType: sourceItemType.ToDisplayString(
                                    SymbolDisplayFormat.FullyQualifiedFormat
                                ),
                                DestItemType: destItemType.ToDisplayString(
                                    SymbolDisplayFormat.FullyQualifiedFormat
                                ),
                                ItemPropertyMaps: itemMaps
                            )
                        );
                    }
                }
                else if (!isSourceList && !isDestList)
                {
                    simpleMaps.Add(new PropertyMap(sourceProp.Name, destProp.Name));
                }
            }
        }

        return (simpleMaps, listMaps);
    }

    public static List<PropertyMap> GetPropertyMaps(
        INamedTypeSymbol? source,
        INamedTypeSymbol? destination
    )
    {
        if (source is null || destination is null)
            return new List<PropertyMap>();

        var sourceProps = source
            .GetMembers()
            .OfType<IPropertySymbol>()
            .ToDictionary(p => p.Name, p => p, StringComparer.OrdinalIgnoreCase);

        var destProps = destination
            .GetMembers()
            .OfType<IPropertySymbol>()
            .Where(p => p.SetMethod is not null)
            .ToList();

        var maps = new List<PropertyMap>();
        foreach (var destProp in destProps)
        {
            if (sourceProps.TryGetValue(destProp.Name, out var sourceProp))
            {
                maps.Add(new PropertyMap(sourceProp.Name, destProp.Name));
            }
        }
        return maps;
    }

    private static bool IsListType(ITypeSymbol type)
    {
        return type is INamedTypeSymbol namedType
            && (
                namedType
                    .ConstructedFrom.ToDisplayString()
                    .StartsWith("System.Collections.Generic.List<")
                || namedType
                    .ConstructedFrom.ToDisplayString()
                    .StartsWith("System.Collections.Generic.IList<")
                || namedType
                    .ConstructedFrom.ToDisplayString()
                    .StartsWith("System.Collections.Generic.IEnumerable<")
            );
    }

    private static INamedTypeSymbol? GetListItemType(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol namedType && namedType.TypeArguments.Length > 0)
        {
            return namedType.TypeArguments[0] as INamedTypeSymbol;
        }
        return null;
    }

    private static bool IsRepeatedType(ITypeSymbol type)
    {
        return type is INamedTypeSymbol namedType
            && namedType.ConstructedFrom.ToDisplayString().Contains("RepeatedField<");
    }

    private static INamedTypeSymbol? GetRepeatedItemType(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol namedType && namedType.TypeArguments.Length > 0)
        {
            return namedType.TypeArguments[0] as INamedTypeSymbol;
        }
        return null;
    }

    private static string GenerateGrpcServices(IReadOnlyList<GrpcEndpointInfo> endpoints)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// Auto-generated by LinKit.Generator");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using Grpc.Core;");
        sb.AppendLine("using LinKit.Core.Cqrs;");
        sb.AppendLine("using LinKit.Core.Abstractions;");
        sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");
        sb.AppendLine("using Microsoft.Extensions.Logging;");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.ComponentModel.DataAnnotations;");
        sb.AppendLine("using System.Threading.Tasks;");
        sb.AppendLine();

        var endpointsByService = endpoints.GroupBy(e => e.ServiceBaseType);

        foreach (var serviceGroup in endpointsByService)
        {
            var serviceBaseType = serviceGroup.Key;
            var baseClassName = serviceBaseType.Split('.').Last();
            var generatedClassName = $"LinKit{baseClassName.Replace("Base", "")}";

            var namespaceParts = serviceBaseType.Split('.');
            var nsWithGlobal = string.Join(".", namespaceParts.Take(namespaceParts.Length - 2));
            if (string.IsNullOrEmpty(nsWithGlobal))
                nsWithGlobal = "LinKit.Generated.Grpc";
            var ns = nsWithGlobal.StartsWith("global::")
                ? nsWithGlobal.Substring("global::".Length)
                : nsWithGlobal;

            sb.AppendLine($"namespace {ns}");
            sb.AppendLine("{");
            sb.AppendLine("    [RegisterService(Lifetime.Scoped)]");
            sb.AppendLine($"    public sealed class {generatedClassName} : {serviceBaseType}");
            sb.AppendLine("    {");
            sb.AppendLine("        private readonly IMediator _mediator;");
            sb.AppendLine($"        private readonly ILogger<{generatedClassName}>? _logger;");
            sb.AppendLine();

            sb.AppendLine(
                $"        public {generatedClassName}(IMediator mediator, IServiceProvider serviceProvider)"
            );
            sb.AppendLine("        {");
            sb.AppendLine("            _mediator = mediator;");
            sb.AppendLine(
                $"            _logger = serviceProvider.GetService<ILogger<{generatedClassName}>>();"
            );
            sb.AppendLine("        }");

            foreach (var endpoint in serviceGroup)
            {
                var requestMappings = endpoint.RequestPropertyMaps.Any()
                    ? $" {{ {string.Join(", ", endpoint.RequestPropertyMaps.Select(m => $"{m.DestProperty} = request.{m.SourceProperty}"))} }}"
                    : "";
                var mediatorMethod = endpoint.IsCqrsQuery ? "QueryAsync" : "SendAsync";
                var cqrsResponseIsNullable = endpoint.CqrsResponseType.EndsWith("?");

                sb.AppendLine();
                sb.AppendLine(
                    $"        public override async Task<{endpoint.GrpcResponseType}> {endpoint.GrpcMethodName}({endpoint.GrpcRequestType} request, ServerCallContext context)"
                );
                sb.AppendLine("        {");
                sb.AppendLine("            try");
                sb.AppendLine("            {");
                sb.AppendLine(
                    $"                LinKit.Core.Grpc.GrpcContextAccessor.Current = context;"
                );
                sb.AppendLine(
                    $"                var cqrsRequest = new {endpoint.CqrsRequestType}(){requestMappings};"
                );
                sb.AppendLine(
                    $"                var cqrsResult = await _mediator.{mediatorMethod}(cqrsRequest, context.CancellationToken);"
                );

                if (cqrsResponseIsNullable)
                {
                    sb.AppendLine("                if (cqrsResult is null)");
                    sb.AppendLine("                {");
                    sb.AppendLine(
                        "                    throw new RpcException(new Status(StatusCode.NotFound, \"Resource not found.\"));"
                    );
                    sb.AppendLine("                }");
                }

                sb.AppendLine(
                    $"                var grpcResponse = new {endpoint.GrpcResponseType}();"
                );

                if (endpoint.ResponsePropertyMaps.Any())
                {
                    var responseMappings = string.Join(
                        ", ",
                        endpoint.ResponsePropertyMaps.Select(m =>
                            $"{m.DestProperty} = cqrsResult.{m.SourceProperty}"
                        )
                    );
                    sb.AppendLine(
                        $"                grpcResponse = new {endpoint.GrpcResponseType} {{ {responseMappings} }};"
                    );
                }

                if (endpoint.ResponseListPropertyMaps.Any())
                {
                    foreach (var listMap in endpoint.ResponseListPropertyMaps)
                    {
                        sb.AppendLine(
                            $"                foreach (var item in cqrsResult.{listMap.SourceProperty})"
                        );
                        sb.AppendLine("                {");
                        sb.AppendLine(
                            $"                    var grpcItem = new {listMap.DestItemType} {{ {string.Join(", ", listMap.ItemPropertyMaps.Select(m => $"{m.DestProperty} = item.{m.SourceProperty}"))} }};"
                        );
                        sb.AppendLine(
                            $"                    grpcResponse.{listMap.DestProperty}.Add(grpcItem);"
                        );
                        sb.AppendLine("                }");
                    }
                }

                sb.AppendLine("                return grpcResponse;");

                sb.AppendLine("            }");
                sb.AppendLine("            catch (ValidationException ex)");
                sb.AppendLine("            {");
                sb.AppendLine(
                    "                throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));"
                );
                sb.AppendLine("            }");
                sb.AppendLine("            catch (Exception ex)");
                sb.AppendLine("            {");
                sb.AppendLine(
                    $"                _logger?.LogError(ex, \"An unexpected error occurred while handling {endpoint.GrpcMethodName}.\");"
                );
                sb.AppendLine(
                    @"                throw new RpcException(new Status(StatusCode.Internal, ""An internal error occurred.""));"
                );
                sb.AppendLine("            }");
                sb.AppendLine("        }");
                sb.AppendLine();
            }

            sb.AppendLine("    }");
            sb.AppendLine("}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public static IncrementalValueProvider<IReadOnlyList<GrpcServiceInfo>> GetServices(
        IncrementalGeneratorInitializationContext context
    )
    {
        IncrementalValuesProvider<GrpcEndpointInfo?> grpcDeclarations = context
            .SyntaxProvider.ForAttributeWithMetadataName(
                GrpcEndpointAttributeName,
                predicate: (node, _) => node is ClassDeclarationSyntax or RecordDeclarationSyntax,
                transform: (ctx, _) => GetGrpcEndpointInfo(ctx)
            )
            .Where(info => info is not null);

        return grpcDeclarations
            .Collect()
            .Select(
                (endpoints, _) =>
                {
                    
                    var services = new List<GrpcServiceInfo>();

                    return (IReadOnlyList<GrpcServiceInfo>)services;
                }
            );
    }
}

// Thêm record cho service info nếu chưa có
internal record GrpcServiceInfo(string RegistrationCode);
