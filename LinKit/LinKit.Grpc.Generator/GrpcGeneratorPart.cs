using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace LinKit.Grpc.Generator;

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
    bool IsCqrsQuery,
    bool IsCommandWithoutResult,
    string? MediatorKeyedService
);

internal record PropertyMap(string SourceProperty, string DestProperty, string? TransformTemplate = null);

internal record ListPropertyMap(
    string SourceProperty,
    string DestProperty,
    string SourceItemType,
    string DestItemType,
    IReadOnlyList<PropertyMap> ItemPropertyMaps
);

internal record ServerTransformationResult
{
    public GrpcEndpointInfo? EndpointInfo { get; init; }
    public Diagnostic? Diagnostic { get; init; }
}

internal static class GrpcGeneratorPart
{
    private const string GrpcEndpointAttributeName = "LinKit.Grpc.GrpcEndpointAttribute";
    private const string IQueryInterfaceName = "LinKit.Core.Cqrs.IQuery";
    private const string ICommandInterfaceName = "LinKit.Core.Cqrs.ICommand";

    public static void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<ServerTransformationResult> grpcDeclarations =
            context.SyntaxProvider.ForAttributeWithMetadataName(
                GrpcEndpointAttributeName,
                predicate: (node, _) => node is ClassDeclarationSyntax or RecordDeclarationSyntax,
                transform: (ctx, _) => GetGrpcEndpointInfo(ctx)
            );

        // Luồng lỗi
        var diagnostics = grpcDeclarations
            .Where(x => x.Diagnostic is not null)
            .Select((x, _) => x.Diagnostic!);

        context.RegisterSourceOutput(
            diagnostics,
            (spc, diagnostic) => spc.ReportDiagnostic(diagnostic)
        );

        // Luồng hợp lệ
        var validEndpoints = grpcDeclarations
            .Where(x => x.EndpointInfo is not null)
            .Select((x, _) => x.EndpointInfo!);

        context.RegisterSourceOutput(
            validEndpoints.Collect(),
            (spc, endpoints) =>
            {
                if (endpoints.IsEmpty)
                {
                    return;
                }
                var source = GenerateGrpcServices(endpoints);
                spc.AddSource("Grpc.Services.g.cs", SourceText.From(source, Encoding.UTF8));
            }
        );
    }

    private static ServerTransformationResult GetGrpcEndpointInfo(
        GeneratorAttributeSyntaxContext context
    )
    {
        if (context.TargetSymbol is not INamedTypeSymbol cqrsRequestSymbol)
        {
            return new ServerTransformationResult(); // Rỗng, không phải null
        }

        var attributeData = context.Attributes[0];
        var attributeLocation =
            attributeData.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? Location.None;

        if (
            attributeData.ConstructorArguments.Length < 2
            || attributeData.ConstructorArguments[0].Value is not INamedTypeSymbol serviceBaseSymbol
            || attributeData.ConstructorArguments[1].Value is not string methodName
        )
        {
            var diag = Diagnostic.Create(
                ServerDiagnostics.LKG104_InvalidAttributeUsage,
                attributeLocation
            );
            return new ServerTransformationResult { Diagnostic = diag };
        }

        var rpcMethod = serviceBaseSymbol
            .GetMembers(methodName)
            .OfType<IMethodSymbol>()
            .FirstOrDefault(m =>
                m.Name == methodName
                && !m.IsStatic
                && m.Parameters.Length == 2
                && m.ReturnType.Name == "Task"
            ); // Chữ ký của gRPC service method là Task<TResponse>

        if (rpcMethod is null)
        {
            var diag = Diagnostic.Create(
                ServerDiagnostics.LKG101_MethodNotFound,
                attributeLocation,
                methodName,
                serviceBaseSymbol.Name
            );
            return new ServerTransformationResult { Diagnostic = diag };
        }

        var grpcRequestSymbol = rpcMethod.Parameters[0].Type as INamedTypeSymbol;
        var grpcResponseSymbol =
            (rpcMethod.ReturnType as INamedTypeSymbol)?.TypeArguments.FirstOrDefault()
            as INamedTypeSymbol;

        if (
            grpcRequestSymbol is null
            || grpcResponseSymbol is null
            || rpcMethod.Parameters[1].Type.Name != "ServerCallContext"
        )
        {
            var diag = Diagnostic.Create(
                ServerDiagnostics.LKG102_InvalidMethodSignature,
                attributeLocation,
                methodName
            );
            return new ServerTransformationResult { Diagnostic = diag };
        }

        var cqrsInterface = cqrsRequestSymbol.AllInterfaces.FirstOrDefault(i =>
            i.ToDisplayString().StartsWith(ICommandInterfaceName)
            || // ICommand...
            i.ToDisplayString().StartsWith(IQueryInterfaceName) // IQuery...
        );

        if (cqrsInterface is null)
        {
            var diag = Diagnostic.Create(
                ServerDiagnostics.LKG103_MissingCqrsInterface,
                context.TargetNode.GetLocation(),
                cqrsRequestSymbol.Name
            );
            return new ServerTransformationResult { Diagnostic = diag };
        }

        string cqrsResponseType;
        bool isCommandWithoutResult = false;
        INamedTypeSymbol? cqrsResponseSymbol = null;

        if (cqrsInterface.TypeArguments.Length > 0)
        {
            cqrsResponseSymbol = cqrsInterface.TypeArguments[0] as INamedTypeSymbol;
            if (cqrsResponseSymbol is null)
            {
                // Không thể phân giải kiểu generic T từ ICommand<T>
                var diag = Diagnostic.Create(
                    ServerDiagnostics.LKG105_UnresolvedResponseType,
                    context.TargetNode.GetLocation(),
                    cqrsRequestSymbol.Name
                );
                return new ServerTransformationResult { Diagnostic = diag };
            }
            cqrsResponseType = cqrsResponseSymbol.ToDisplayString(
                SymbolDisplayFormat.FullyQualifiedFormat
            );
        }
        else
        {
            cqrsResponseType = "System.ValueTuple";
            isCommandWithoutResult = true;
        }

        // Logic còn lại của bạn giữ nguyên...
        var requestMaps = GetPropertyMaps(cqrsRequestSymbol, grpcRequestSymbol); // Sửa lại thứ tự tham số
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

        var mediatorKeyedService = attributeData.NamedArguments
            .FirstOrDefault(kvp => kvp.Key == "MediatorKeyedService").Value.Value as string;

        var endpointInfo = new GrpcEndpointInfo(
            CqrsRequestType: cqrsRequestSymbol.ToDisplayString(
                SymbolDisplayFormat.FullyQualifiedFormat
            ),
            CqrsResponseType: cqrsResponseType,
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
            IsCqrsQuery: isQuery,
            IsCommandWithoutResult: isCommandWithoutResult,
            MediatorKeyedService: mediatorKeyedService
        );

        return new ServerTransformationResult { EndpointInfo = endpointInfo };
    }

    private static (List<PropertyMap>, List<ListPropertyMap>) GetResponseMaps(
        INamedTypeSymbol? source,
        INamedTypeSymbol? destination
    )
    {
        if (source is null || destination is null)
        {
            return (new List<PropertyMap>(), new List<ListPropertyMap>());
        }

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
                    var transform = GetTransformTemplate(sourceProp.Type, destProp.Type);
                    simpleMaps.Add(new PropertyMap(sourceProp.Name, destProp.Name, transform));
                }
            }
        }

        return (simpleMaps, listMaps);
    }

    private static string? GetTransformTemplate(ITypeSymbol sourceType, ITypeSymbol destType)
    {
        var srcStr = sourceType.ToDisplayString();
        var destStr = destType.ToDisplayString();

        if (srcStr == destStr) return null;

        // Guid <-> string
        if (srcStr == "System.Guid" && destStr == "string")
            return "{0}.ToString()";
        if (srcStr == "string" && destStr == "System.Guid")
            return "System.Guid.Parse({0})";

        // DateTime / DateTimeOffset <-> Google.Protobuf.WellKnownTypes.Timestamp
        if (destStr.Contains("Google.Protobuf.WellKnownTypes.Timestamp"))
        {
            if (srcStr == "System.DateTimeOffset")
                return "Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset({0})";
            if (srcStr == "System.DateTime")
                return "Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime({0}.Kind == System.DateTimeKind.Utc ? {0} : {0}.ToUniversalTime())";
        }
        if (srcStr.Contains("Google.Protobuf.WellKnownTypes.Timestamp"))
        {
            if (destStr == "System.DateTimeOffset")
                return "{0}.ToDateTimeOffset()";
            if (destStr == "System.DateTime")
                return "{0}.ToDateTime()";
        }

        // Enum <-> int
        if (sourceType.TypeKind == TypeKind.Enum && destStr == "int")
            return "(int){0}";
        if (srcStr == "int" && destType.TypeKind == TypeKind.Enum)
            return $"({destType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}){{0}}";

        return null;
    }

    public static List<PropertyMap> GetPropertyMaps(
        INamedTypeSymbol? source,
        INamedTypeSymbol? destination
    )
    {
        if (source is null || destination is null)
        {
            return new List<PropertyMap>();
        }

        static IEnumerable<IPropertySymbol> GetAllProperties(INamedTypeSymbol type)
        {
            var current = type;
            while (current != null)
            {
                foreach (var member in current.GetMembers().OfType<IPropertySymbol>())
                {
                    yield return member;
                }
                current = current.BaseType;
            }
        }

        var sourceProps = GetAllProperties(source)
            .Where(p => p.GetMethod is not null)
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToDictionary(p => p.Name, p => p, StringComparer.OrdinalIgnoreCase);

        var destProps = GetAllProperties(destination)
            .Where(p => p.SetMethod is not null)
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        var maps = new List<PropertyMap>();
        foreach (var destProp in destProps)
        {
            if (sourceProps.TryGetValue(destProp.Name, out var sourceProp))
            {
                var transform = GetTransformTemplate(sourceProp.Type, destProp.Type);
                maps.Add(new PropertyMap(sourceProp.Name, destProp.Name, transform));
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
        sb.AppendLine("// <auto-generated> by LinKit.Generator");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using Grpc.Core;");
        sb.AppendLine("using LinKit.Core.Cqrs;");
        sb.AppendLine("using LinKit.Grpc;");
        sb.AppendLine("using LinKit.Core.Abstractions;");
        sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");
        sb.AppendLine("using Microsoft.Extensions.Logging;");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
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
            {
                nsWithGlobal = "LinKit.Generated.Grpc";
            }

            var ns = nsWithGlobal.StartsWith("global::")
                ? nsWithGlobal.Substring("global::".Length)
                : nsWithGlobal;

            sb.AppendLine($"namespace {ns}");
            sb.AppendLine("{");
            sb.AppendLine($"    public sealed class {generatedClassName} : {serviceBaseType}");
            sb.AppendLine("    {");
            sb.AppendLine("        private readonly IMediator _mediator;");
            sb.AppendLine("        private readonly IServiceProvider _serviceProvider;");
            sb.AppendLine($"        private readonly ILogger<{generatedClassName}>? _logger;");
            sb.AppendLine();

            sb.AppendLine(
                $"        public {generatedClassName}(IMediator mediator, IServiceProvider serviceProvider)"
            );
            sb.AppendLine("        {");
            sb.AppendLine("            _mediator = mediator;");
            sb.AppendLine("            _serviceProvider = serviceProvider;");
            sb.AppendLine(
                $"            _logger = serviceProvider.GetService<ILogger<{generatedClassName}>>();"
            );
            sb.AppendLine("        }");
            sb.AppendLine();

            foreach (var endpoint in serviceGroup)
            {
                var requestMappings = endpoint.RequestPropertyMaps.Any()
                    ? $" {{ {string.Join(", ", endpoint.RequestPropertyMaps.Select(m => m.TransformTemplate != null ? $"{m.DestProperty} = {string.Format(m.TransformTemplate, $"request.{m.SourceProperty}")}" : $"{m.DestProperty} = request.{m.SourceProperty}"))} }}"
                    : "()";
                var mediatorMethod = endpoint.IsCqrsQuery ? "QueryAsync" : "SendAsync";
                var cqrsResponseIsNullable = endpoint.CqrsResponseType.EndsWith("?");

                var mediatorAccessor = string.IsNullOrEmpty(endpoint.MediatorKeyedService)
                    ? "_mediator"
                    : $"(_serviceProvider.GetKeyedService<LinKit.Core.Cqrs.IMediator>(\"{endpoint.MediatorKeyedService}\") ?? _mediator)";

                sb.AppendLine(
                    $"        public override async Task<{endpoint.GrpcResponseType}> {endpoint.GrpcMethodName}({endpoint.GrpcRequestType} request, ServerCallContext context)"
                );
                sb.AppendLine("        {");
                sb.AppendLine("            using var _ = LinKit.Grpc.GrpcContextAccessor.BeginScope(context);");
                sb.AppendLine("            try");
                sb.AppendLine("            {");
                sb.AppendLine(
                    $"                var cqrsRequest = new {endpoint.CqrsRequestType}{requestMappings};"
                );
                sb.AppendLine(
                    endpoint.IsCommandWithoutResult
                        ? $"                await {mediatorAccessor}.SendAsync(cqrsRequest, context.CancellationToken);"
                        : $"                var cqrsResult = await {mediatorAccessor}.{mediatorMethod}<{endpoint.CqrsResponseType.TrimEnd('?')}>(cqrsRequest, context.CancellationToken);"
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
                if (!endpoint.IsCommandWithoutResult)
                {
                    sb.AppendLine(
                        $"                var grpcResponse = new {endpoint.GrpcResponseType}();"
                    );

                    if (endpoint.ResponsePropertyMaps.Any())
                    {
                        var responseMappings = string.Join(
                            ", ",
                            endpoint.ResponsePropertyMaps.Select(m =>
                                m.TransformTemplate != null
                                    ? $"{m.DestProperty} = {string.Format(m.TransformTemplate, $"cqrsResult.{m.SourceProperty}")}"
                                    : $"{m.DestProperty} = cqrsResult.{m.SourceProperty}"
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
                                $"                    var grpcItem = new {listMap.DestItemType} {{ {string.Join(", ", listMap.ItemPropertyMaps.Select(m => m.TransformTemplate != null ? $"{m.DestProperty} = {string.Format(m.TransformTemplate, $"item.{m.SourceProperty}")}" : $"{m.DestProperty} = item.{m.SourceProperty}"))} }};"
                            );
                            sb.AppendLine(
                                $"                    grpcResponse.{listMap.DestProperty}.Add(grpcItem);"
                            );
                            sb.AppendLine("                }");
                        }
                    }

                    sb.AppendLine("                return grpcResponse;");
                }
                else
                {
                    sb.AppendLine($"                return new {endpoint.GrpcResponseType}();");
                }

                sb.AppendLine("            }");
                sb.AppendLine("            catch (RpcException)");
                sb.AppendLine("            {");
                sb.AppendLine("                throw;");
                sb.AppendLine("            }");
                sb.AppendLine("            catch (ValidationException ex)");
                sb.AppendLine("            {");
                sb.AppendLine(
                    "                throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));"
                );
                sb.AppendLine("            }");
                sb.AppendLine("            catch (ArgumentException ex)");
                sb.AppendLine("            {");
                sb.AppendLine(
                    "                throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));"
                );
                sb.AppendLine("            }");
                sb.AppendLine("            catch (KeyNotFoundException ex)");
                sb.AppendLine("            {");
                sb.AppendLine(
                    "                throw new RpcException(new Status(StatusCode.NotFound, ex.Message));"
                );
                sb.AppendLine("            }");
                sb.AppendLine("            catch (UnauthorizedAccessException ex)");
                sb.AppendLine("            {");
                sb.AppendLine(
                    "                throw new RpcException(new Status(StatusCode.Unauthenticated, ex.Message));"
                );
                sb.AppendLine("            }");
                sb.AppendLine("            catch (InvalidOperationException ex)");
                sb.AppendLine("            {");
                sb.AppendLine(
                    "                throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));"
                );
                sb.AppendLine("            }");
                sb.AppendLine("            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)");
                sb.AppendLine("            {");
                sb.AppendLine(
                    "                throw new RpcException(new Status(StatusCode.Cancelled, \"The operation was cancelled by client.\"));"
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

        // Generate MapLinKitGrpcServices endpoint extension
        sb.AppendLine("namespace Microsoft.AspNetCore.Builder");
        sb.AppendLine("{");
        sb.AppendLine("    public static class LinKitGrpcEndpointRouteBuilderExtensions");
        sb.AppendLine("    {");
        sb.AppendLine("        public static Microsoft.AspNetCore.Routing.IEndpointRouteBuilder MapLinKitGrpcServices(this Microsoft.AspNetCore.Routing.IEndpointRouteBuilder endpoints)");
        sb.AppendLine("        {");
        foreach (var serviceGroup in endpointsByService)
        {
            var serviceBaseType = serviceGroup.Key;
            var baseClassName = serviceBaseType.Split('.').Last();
            var generatedClassName = $"LinKit{baseClassName.Replace("Base", "")}";

            var namespaceParts = serviceBaseType.Split('.');
            var nsWithGlobal = string.Join(".", namespaceParts.Take(namespaceParts.Length - 2));
            if (string.IsNullOrEmpty(nsWithGlobal))
            {
                nsWithGlobal = "LinKit.Generated.Grpc";
            }

            var ns = nsWithGlobal.StartsWith("global::")
                ? nsWithGlobal.Substring("global::".Length)
                : nsWithGlobal;

            sb.AppendLine($"            GrpcEndpointRouteBuilderExtensions.MapGrpcService<{ns}.{generatedClassName}>(endpoints);");
        }
        sb.AppendLine("            return endpoints;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }

    public static IncrementalValueProvider<IReadOnlyList<GrpcServiceInfo>> GetServices(
        IncrementalGeneratorInitializationContext context
    )
    {
        IncrementalValuesProvider<GrpcEndpointInfo> grpcDeclarations = context
            .SyntaxProvider.ForAttributeWithMetadataName(
                GrpcEndpointAttributeName,
                predicate: (node, _) => node is ClassDeclarationSyntax or RecordDeclarationSyntax,
                transform: (ctx, _) => GetGrpcEndpointInfo(ctx)
            )
            .Where(info => info is not null && info.EndpointInfo is not null)
            .Select((x, _) => x.EndpointInfo!);

        return grpcDeclarations
            .Collect()
            .Select(
                (endpoints, _) =>
                {
                    var services = new List<GrpcServiceInfo>();
                    var endpointsByService = endpoints.GroupBy(e => e.ServiceBaseType);

                    foreach (var serviceGroup in endpointsByService)
                    {
                        var serviceBaseType = serviceGroup.Key;
                        var baseClassName = serviceBaseType.Split('.').Last();
                        var generatedClassName = $"LinKit{baseClassName.Replace("Base", "")}";

                        var namespaceParts = serviceBaseType.Split('.');
                        var nsWithGlobal = string.Join(".", namespaceParts.Take(namespaceParts.Length - 2));
                        if (string.IsNullOrEmpty(nsWithGlobal))
                        {
                            nsWithGlobal = "LinKit.Generated.Grpc";
                        }

                        var ns = nsWithGlobal.StartsWith("global::")
                            ? nsWithGlobal.Substring("global::".Length)
                            : nsWithGlobal;

                        services.Add(new GrpcServiceInfo($"services.AddScoped<{ns}.{generatedClassName}>();"));
                    }

                    return (IReadOnlyList<GrpcServiceInfo>)services;
                }
            );
    }
}
