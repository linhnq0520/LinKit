using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace LinKit.Grpc.Generator;

internal record GrpcClientEndpointInfo(
    string CqrsRequestType,
    string CqrsResponseType,
    string GrpcClientType,
    string GrpcMethodName,
    string GrpcRequestType,
    string GrpcResponseType,
    IReadOnlyList<PropertyMap> RequestPropertyMaps,
    IReadOnlyList<PropertyMap> ResponsePropertyMaps,
    IReadOnlyList<ConstructorParameterMap> ResponseConstructorParameters,
    bool IsCqrsQuery,
    bool IsVoidCommand
);

internal record ConstructorParameterMap(
    string ParameterName,
    string SourceProperty,
    string ParameterType
);

internal record TransformationResult
{
    public GrpcClientEndpointInfo? EndpointInfo { get; init; }
    public Diagnostic? Diagnostic { get; init; }
}

internal static class GrpcClientGeneratorPart
{
    private const string GrpcClientAttributeName = "LinKit.Grpc.GrpcClientAttribute";
    private const string IQueryInterfaceName = "LinKit.Core.Cqrs.IQuery";
    private const string ICommandInterfaceName = "LinKit.Core.Cqrs.ICommand";

    public static IncrementalValueProvider<IReadOnlyList<GrpcClientServiceInfo>> GetServices(
        IncrementalGeneratorInitializationContext context
    )
    {
        IncrementalValuesProvider<GrpcClientEndpointInfo> validEndpoints = context
            .SyntaxProvider.ForAttributeWithMetadataName(
                GrpcClientAttributeName,
                predicate: (node, _) => node is ClassDeclarationSyntax or RecordDeclarationSyntax,
                transform: (ctx, _) => GetGrpcClientEndpointInfo(ctx)
            )
            .Where(x => x?.EndpointInfo is not null)
            .Select((x, _) => x!.EndpointInfo!);

        return validEndpoints
            .Collect()
            .Select(
                (endpoints, _) =>
                {
                    var services = new List<GrpcClientServiceInfo>();
                    if (endpoints.Any())
                    {
                        services.Add(
                            new GrpcClientServiceInfo(
                                "services.AddTransient<LinKit.Grpc.IGrpcMediator, LinKit.Generated.Grpc.GrpcClientMediator>();"
                            )
                        );
                    }
                    return (IReadOnlyList<GrpcClientServiceInfo>)services;
                }
            );
    }

    public static void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<TransformationResult> transformationPipeline = context
            .SyntaxProvider.ForAttributeWithMetadataName(
                GrpcClientAttributeName,
                predicate: (node, _) => node is ClassDeclarationSyntax or RecordDeclarationSyntax,
                transform: (ctx, _) => GetGrpcClientEndpointInfo(ctx)
            )
            .Where(x => x is not null)!;

        IncrementalValuesProvider<Diagnostic> diagnostics = transformationPipeline
            .Where(x => x.Diagnostic is not null)
            .Select((x, _) => x.Diagnostic!);

        context.RegisterSourceOutput(
            diagnostics,
            (spc, diagnostic) =>
            {
                spc.ReportDiagnostic(diagnostic);
            }
        );

        IncrementalValuesProvider<GrpcClientEndpointInfo> validEndpoints = transformationPipeline
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

                var source = GenerateGrpcMediator(endpoints);
                spc.AddSource("Grpc.ClientMediator.g.cs", SourceText.From(source, Encoding.UTF8));
            }
        );
    }

    private static TransformationResult? GetGrpcClientEndpointInfo(
        GeneratorAttributeSyntaxContext context
    )
    {
        if (context.TargetSymbol is not INamedTypeSymbol cqrsRequestSymbol)
        {
            return null;
        }

        var attributeData = context.Attributes[0];
        var attributeLocation =
            attributeData.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? Location.None;

        if (
            attributeData.ConstructorArguments.Length < 2
            || attributeData.ConstructorArguments[0].Value is not INamedTypeSymbol grpcClientSymbol
            || attributeData.ConstructorArguments[1].Value is not string methodName
        )
        {
            var diagnostic = Diagnostic.Create(
                Diagnostics.LKG004_InvalidAttributeUsage,
                attributeLocation
            );
            return new TransformationResult { Diagnostic = diagnostic };
        }

        var rpcMethod = grpcClientSymbol
            .GetMembers(methodName)
            .OfType<IMethodSymbol>()
            .FirstOrDefault(m =>
                m.Parameters.Length >= 1
                && m.Name == methodName
                && m.ReturnType.Name.Contains("AsyncUnaryCall")
            );

        if (rpcMethod is null)
        {
            var diagnostic = Diagnostic.Create(
                Diagnostics.LKG001_MethodNotFound,
                attributeLocation,
                methodName,
                grpcClientSymbol.Name
            );
            return new TransformationResult { Diagnostic = diagnostic };
        }

        var grpcRequestSymbol = rpcMethod.Parameters[0].Type as INamedTypeSymbol;
        var returnTypeSymbol = rpcMethod.ReturnType as INamedTypeSymbol;
        var grpcResponseSymbol =
            returnTypeSymbol?.TypeArguments.FirstOrDefault() as INamedTypeSymbol;

        if (grpcRequestSymbol is null || grpcResponseSymbol is null)
        {
            var diagnostic = Diagnostic.Create(
                Diagnostics.LKG002_InvalidMethodSignature,
                attributeLocation,
                methodName
            );
            return new TransformationResult { Diagnostic = diagnostic };
        }

        var cqrsInterface = cqrsRequestSymbol.AllInterfaces.FirstOrDefault(i =>
            i.ToDisplayString().StartsWith("LinKit.Core.Cqrs.IQuery")
            || i.ToDisplayString().StartsWith("LinKit.Core.Cqrs.ICommand")
        );

        if (cqrsInterface is null)
        {
            var diagnostic = Diagnostic.Create(
                Diagnostics.LKG003_MissingCqrsInterface,
                context.TargetNode.GetLocation(),
                cqrsRequestSymbol.Name
            );
            return new TransformationResult { Diagnostic = diagnostic };
        }

        string cqrsResponseType;
        bool isVoidCommand = false;
        if (cqrsInterface.TypeArguments.Length > 0)
        {
            cqrsResponseType = cqrsInterface
                .TypeArguments[0]
                .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        }
        else
        {
            cqrsResponseType = "LinKit.Core.Cqrs.Unit";
            isVoidCommand = true;
        }

        var requestMaps = GrpcGeneratorPart.GetPropertyMaps(cqrsRequestSymbol, grpcRequestSymbol);
        var cqrsResponseSymbol = cqrsInterface.TypeArguments.FirstOrDefault() as INamedTypeSymbol;

        IReadOnlyList<PropertyMap> responseMaps = new List<PropertyMap>();
        IReadOnlyList<ConstructorParameterMap> responseConstructorParameters =
            new List<ConstructorParameterMap>();
        if (!isVoidCommand && cqrsResponseSymbol != null)
        {
            var constructor = cqrsResponseSymbol
                .GetMembers()
                .OfType<IMethodSymbol>()
                .FirstOrDefault(m =>
                    m.MethodKind == MethodKind.Constructor && m.Parameters.Length > 0
                );

            if (constructor != null)
            {
                var paramMaps = new List<ConstructorParameterMap>();
                var grpcProps = grpcResponseSymbol
                    .GetMembers()
                    .OfType<IPropertySymbol>()
                    .Where(p => !p.IsStatic)
                    .ToDictionary(p => p.Name, p => p, StringComparer.OrdinalIgnoreCase);

                foreach (var param in constructor.Parameters)
                {
                    if (grpcProps.TryGetValue(param.Name, out var grpcProp))
                    {
                        paramMaps.Add(
                            new ConstructorParameterMap(
                                ParameterName: param.Name,
                                SourceProperty: grpcProp.Name,
                                ParameterType: param.Type.ToDisplayString(
                                    SymbolDisplayFormat.FullyQualifiedFormat
                                )
                            )
                        );
                    }
                }
                responseConstructorParameters = paramMaps;

                if (paramMaps.Count == constructor.Parameters.Length)
                {
                    responseMaps = new List<PropertyMap>();
                }
                else
                {
                    responseMaps = GrpcGeneratorPart.GetPropertyMaps(
                        grpcResponseSymbol,
                        cqrsResponseSymbol
                    );
                }
            }
            else
            {
                responseMaps = GrpcGeneratorPart.GetPropertyMaps(
                    grpcResponseSymbol,
                    cqrsResponseSymbol
                );
            }
        }

        bool isQuery = cqrsInterface
            .OriginalDefinition.ToDisplayString()
            .Contains(IQueryInterfaceName);

        return new TransformationResult
        {
            EndpointInfo = new GrpcClientEndpointInfo(
                CqrsRequestType: cqrsRequestSymbol.ToDisplayString(
                    SymbolDisplayFormat.FullyQualifiedFormat
                ),
                CqrsResponseType: cqrsResponseType,
                GrpcClientType: grpcClientSymbol.ToDisplayString(
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
                ResponseConstructorParameters: responseConstructorParameters,
                IsCqrsQuery: isQuery,
                IsVoidCommand: isVoidCommand
            ),
        };
    }

    private static string GenerateGrpcMediator(IReadOnlyList<GrpcClientEndpointInfo> endpoints)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            @"// <auto-generated> by LinKit.Generator
#nullable enable
using Grpc.Core;
using Grpc.Net.Client;
using LinKit.Core.Cqrs;
using LinKit.Grpc;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core.Interceptors;
"
        );

        sb.AppendLine(
            @"
namespace LinKit.Generated.Grpc
{
    public sealed class GrpcClientMediator : IGrpcMediator
    {
        private readonly IGrpcClientFactory _factory;

        public GrpcClientMediator(IGrpcClientFactory factory)
        {
            _factory = factory;
        }
"
        );

        var voidCommands = endpoints.Where(e => e.IsVoidCommand).ToList();
        sb.AppendLine(
            @"
        public Task SendAsync(ICommand command, CancellationToken cancellationToken = default)
        {
            return (object)command switch
            {"
        );
        foreach (var cmd in voidCommands)
        {
            sb.AppendLine(
                $"                {cmd.CqrsRequestType} c => Handle{cmd.GrpcMethodName}AsVoid(c, cancellationToken),"
            );
        }
        sb.AppendLine(
            "                _ => throw new System.InvalidOperationException($\"No gRPC client endpoint is configured for command type {command.GetType().FullName}.\")"
        );
        sb.AppendLine("            };");
        sb.AppendLine("        }");

        var commandsWithResult = endpoints.Where(e => !e.IsCqrsQuery && !e.IsVoidCommand).ToList();
        sb.AppendLine(
            @"
        public Task<TResponse> SendAsync<TResponse>(ICommand<TResponse> command, CancellationToken cancellationToken = default)
        {
            return (object)command switch
            {"
        );
        foreach (var cmd in commandsWithResult)
        {
            sb.AppendLine(
                $"                {cmd.CqrsRequestType} c => (Task<TResponse>)(object)Handle{cmd.GrpcMethodName}(c, cancellationToken),"
            );
        }
        sb.AppendLine(
            "                _ => throw new System.InvalidOperationException($\"No result-returning gRPC client command is configured for type {command.GetType().FullName}.\")"
        );
        sb.AppendLine("            };");
        sb.AppendLine("        }");

        var queries = endpoints.Where(e => e.IsCqrsQuery).ToList();
        sb.AppendLine(
            @"
        public Task<TResponse> QueryAsync<TResponse>(IQuery<TResponse> query, CancellationToken cancellationToken = default)
        {
            return (object)query switch
            {"
        );
        foreach (var q in queries)
        {
            sb.AppendLine(
                $"                {q.CqrsRequestType} q => (Task<TResponse>)(object)Handle{q.GrpcMethodName}(q, cancellationToken),"
            );
        }
        sb.AppendLine(
            "                _ => throw new System.InvalidOperationException($\"No gRPC client query is configured for type {query.GetType().FullName}.\")"
        );
        sb.AppendLine("            };");
        sb.AppendLine("        }");

        foreach (var endpoint in endpoints)
        {
            string requestMappings;
            if (endpoint.RequestPropertyMaps.Any())
            {
                var assignments = string.Join(
                    ", ",
                    endpoint.RequestPropertyMaps.Select(m =>
                        $"{m.DestProperty} = request.{m.SourceProperty}"
                    )
                );
                requestMappings = $" {{ {assignments} }}";
            }
            else
            {
                requestMappings = "";
            }

            string responseInitialization;
            if (endpoint.ResponseConstructorParameters.Any())
            {
                var constructorArgs = string.Join(
                    ", ",
                    endpoint.ResponseConstructorParameters.Select(p =>
                        $"grpcResponse.{p.SourceProperty}"
                    )
                );
                responseInitialization = $"({constructorArgs})";
            }
            else if (endpoint.ResponsePropertyMaps.Any())
            {
                var assignments = string.Join(
                    ", ",
                    endpoint.ResponsePropertyMaps.Select(m =>
                        $"{m.DestProperty} = grpcResponse.{m.SourceProperty}"
                    )
                );
                responseInitialization = $" {{ {assignments} }}";
            }
            else
            {
                responseInitialization = "()";
            }

            sb.AppendLine(
                $@"
        private async Task<{endpoint.CqrsResponseType}> Handle{endpoint.GrpcMethodName}({endpoint.CqrsRequestType} request, CancellationToken cancellationToken)
        {{
            var channel = _factory.GetChannelFor<{endpoint.GrpcClientType}>();
            var interceptors = _factory.GetInterceptorsFor<{endpoint.GrpcClientType}>();
            var invoker = channel.CreateCallInvoker();
            if(interceptors.Length > 0)
            {{
                invoker = invoker.Intercept(interceptors);
            }}
            var client = new {endpoint.GrpcClientType}(invoker);

            var grpcRequest = new {endpoint.GrpcRequestType}{requestMappings};

            var headers = _factory.GetMetadata();
            var callOptions = new CallOptions(headers: headers, cancellationToken: cancellationToken);

            var grpcResponse = await client.{endpoint.GrpcMethodName}(grpcRequest, callOptions);
"
            );

            if (!endpoint.IsVoidCommand)
            {
                sb.AppendLine(
                    @"
            if (grpcResponse == null)
                throw new RpcException(new Status(StatusCode.NotFound, ""gRPC call returned a null response.""));
"
                );
                sb.AppendLine(
                    $"            return new {endpoint.CqrsResponseType.TrimEnd('?')}{responseInitialization};"
                );
            }
            else
            {
                sb.AppendLine("            return LinKit.Core.Cqrs.Unit.Value;");
            }
            sb.AppendLine("        }");

            if (endpoint.IsVoidCommand)
            {
                sb.AppendLine(
                    $@"
        private async Task Handle{endpoint.GrpcMethodName}AsVoid({endpoint.CqrsRequestType} request, CancellationToken cancellationToken)
        {{
            await Handle{endpoint.GrpcMethodName}(request, cancellationToken);
        }}"
                );
            }
        }

        sb.AppendLine(
            @"    }
}"
        );
        return sb.ToString();
    }
}
