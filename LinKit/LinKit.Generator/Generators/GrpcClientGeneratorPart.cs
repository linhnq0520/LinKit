using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace LinKit.Generator.Generators;

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

internal static class GrpcClientGeneratorPart
{
    private const string GrpcClientAttributeName = "LinKit.Core.Grpc.GrpcClientAttribute";
    private const string IQueryInterfaceName = "LinKit.Core.Cqrs.IQuery";
    private const string ICommandInterfaceName = "LinKit.Core.Cqrs.ICommand";

    public static void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<GrpcClientEndpointInfo?> clientDeclarations = context
            .SyntaxProvider.ForAttributeWithMetadataName(
                GrpcClientAttributeName,
                predicate: (node, _) => node is ClassDeclarationSyntax or RecordDeclarationSyntax,
                transform: (ctx, _) => GetGrpcClientEndpointInfo(ctx)
            )
            .Where(info => info is not null);

        context.RegisterSourceOutput(
            clientDeclarations.Collect(),
            (spc, endpoints) =>
            {
                var validEndpoints = endpoints.OfType<GrpcClientEndpointInfo>().ToList();
                if (!validEndpoints.Any())
                    return;

                var source = GenerateGrpcMediator(validEndpoints);
                spc.AddSource("Grpc.ClientMediator.g.cs", SourceText.From(source, Encoding.UTF8));

                var sourceDI = GenerateGrpcClientDI();
                spc.AddSource(
                    "Grpc.ClientMediatorDependencyInjection.g.cs",
                    SourceText.From(sourceDI, Encoding.UTF8)
                );
            }
        );
    }

    public static IncrementalValueProvider<IReadOnlyList<GrpcClientServiceInfo>> GetServices(
        IncrementalGeneratorInitializationContext context
    )
    {
        IncrementalValuesProvider<GrpcClientEndpointInfo?> clientDeclarations = context
            .SyntaxProvider.ForAttributeWithMetadataName(
                GrpcClientAttributeName,
                predicate: (node, _) => node is ClassDeclarationSyntax or RecordDeclarationSyntax,
                transform: (ctx, _) => GetGrpcClientEndpointInfo(ctx)
            )
            .Where(info => info is not null);

        return clientDeclarations
            .Collect()
            .Select(
                (endpoints, _) =>
                {
                    var validEndpoints = endpoints.OfType<GrpcClientEndpointInfo>().ToList();
                    var services = new List<GrpcClientServiceInfo>();

                    if (validEndpoints.Any())
                    {
                        // Đăng ký mediator
                        services.Add(
                            new GrpcClientServiceInfo(
                                "services.AddTransient<LinKit.Core.Grpc.IGrpcMediator, LinKit.Generated.Grpc.GrpcClientMediator>();"
                            )
                        );
                    }

                    // Nếu có các service khác cần đăng ký, append thêm vào đây

                    return (IReadOnlyList<GrpcClientServiceInfo>)services;
                }
            );
    }

    public static void GenerateNonDIFiles(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<GrpcClientEndpointInfo?> clientDeclarations = context
            .SyntaxProvider.ForAttributeWithMetadataName(
                GrpcClientAttributeName,
                predicate: (node, _) => node is ClassDeclarationSyntax or RecordDeclarationSyntax,
                transform: (ctx, _) => GetGrpcClientEndpointInfo(ctx)
            )
            .Where(info => info is not null);

        context.RegisterSourceOutput(
            clientDeclarations.Collect(),
            (spc, endpoints) =>
            {
                var validEndpoints = endpoints.OfType<GrpcClientEndpointInfo>().ToList();
                if (!validEndpoints.Any())
                    return;

                var source = GenerateGrpcMediator(validEndpoints);
                spc.AddSource("Grpc.ClientMediator.g.cs", SourceText.From(source, Encoding.UTF8));
            }
        );
    }

    private static GrpcClientEndpointInfo? GetGrpcClientEndpointInfo(
        GeneratorAttributeSyntaxContext context
    )
    {
        if (context.TargetSymbol is not INamedTypeSymbol cqrsRequestSymbol)
            return null;
        var attributeData = context.Attributes[0];

        if (
            attributeData.ConstructorArguments.Length < 2
            || attributeData.ConstructorArguments[0].Value is not INamedTypeSymbol grpcClientSymbol
            || attributeData.ConstructorArguments[1].Value is not string methodName
        )
        {
            return null;
        }

        var rpcMethod = grpcClientSymbol
            .GetMembers(methodName)
            .OfType<IMethodSymbol>()
            .FirstOrDefault(m => m.Parameters.Length >= 1 && m.Name == methodName);
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
        if (cqrsInterface is null)
            return null;

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
            cqrsResponseType = "System.ValueTuple";
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
            .StartsWith(IQueryInterfaceName);

        return new GrpcClientEndpointInfo(
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
        );
    }

    private static string GenerateGrpcMediator(IReadOnlyList<GrpcClientEndpointInfo> endpoints)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            @"// Auto-generated by LinKit.Generator
#nullable enable
using Grpc.Core;
using Grpc.Net.Client;
using LinKit.Core.Cqrs;
using LinKit.Core.Abstractions;
using LinKit.Core.Grpc;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Threading;
using System.Threading.Tasks;
"
        );

        var queries = endpoints.Where(e => e.IsCqrsQuery).ToList();
        var commandsWithResult = endpoints.Where(e => !e.IsCqrsQuery && !e.IsVoidCommand).ToList();
        var voidCommands = endpoints.Where(e => e.IsVoidCommand).ToList();

        sb.AppendLine(
            @"
namespace LinKit.Generated.Grpc
{
    public sealed class GrpcClientMediator : IGrpcMediator
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IGrpcChannelProvider _channelProvider;
        private readonly IMetadataProvider? _metadataProvider;

        public GrpcClientMediator(IServiceProvider serviceProvider, IGrpcChannelProvider channelProvider)
        {
            _serviceProvider = serviceProvider;
            _channelProvider = channelProvider;
            _metadataProvider = _serviceProvider.GetService<IMetadataProvider>();
        }

        public Task SendAsync<TCommand>(TCommand command, CancellationToken ct = default) where TCommand : ICommand
        {"
        );
        if (voidCommands.Any())
        {
            sb.AppendLine("            return command switch {");
            foreach (var cmd in voidCommands)
            {
                sb.AppendLine(
                    $"                {cmd.CqrsRequestType} c => (Task)Handle{cmd.GrpcMethodName}(c, ct),"
                );
            }
            sb.AppendLine(
                "                _ => throw new InvalidOperationException($\"No gRPC client endpoint is configured for command type {typeof(TCommand).FullName}.\")"
            );
            sb.AppendLine("            };");
        }
        else
        {
            sb.AppendLine(
                "            throw new InvalidOperationException(\"No void-returning gRPC client commands are configured.\");"
            );
        }
        sb.AppendLine("        }");

        sb.AppendLine(
            @"
        public Task<TResult> SendAsync<TCommand, TResult>(TCommand command, CancellationToken ct = default) where TCommand : ICommand<TResult>
        {"
        );
        if (commandsWithResult.Any())
        {
            sb.AppendLine("            return command switch {");
            foreach (var cmd in commandsWithResult)
            {
                sb.AppendLine(
                    $"                {cmd.CqrsRequestType} c => (Task<TResult>)(object)Handle{cmd.GrpcMethodName}(c, ct),"
                );
            }
            sb.AppendLine(
                "                _ => throw new InvalidOperationException($\"No result-returning gRPC client command is configured for type {typeof(TCommand).FullName}.\")"
            );
            sb.AppendLine("            };");
        }
        else
        {
            sb.AppendLine(
                "            throw new InvalidOperationException(\"No result-returning gRPC client commands are configured.\");"
            );
        }
        sb.AppendLine("        }");

        sb.AppendLine(
            @"
        public Task<TResult> QueryAsync<TQuery, TResult>(TQuery query, CancellationToken ct = default) where TQuery : IQuery<TResult>
        {"
        );
        if (queries.Any())
        {
            sb.AppendLine("            return query switch {");
            foreach (var q in queries)
            {
                sb.AppendLine(
                    $"                {q.CqrsRequestType} q => (Task<TResult>)(object)Handle{q.GrpcMethodName}(q, ct),"
                );
            }
            sb.AppendLine(
                "                _ => throw new InvalidOperationException($\"No gRPC client query is configured for type {typeof(TQuery).FullName}.\")"
            );
            sb.AppendLine("            };");
        }
        else
        {
            sb.AppendLine(
                "            throw new InvalidOperationException(\"No gRPC client queries are configured.\");"
            );
        }
        sb.AppendLine("        }");

        foreach (var endpoint in endpoints)
        {
            var requestMappings = endpoint.RequestPropertyMaps.Any()
                ? $" {{ {string.Join(", ", endpoint.RequestPropertyMaps.Select(m => $"{m.DestProperty} = request.{m.SourceProperty}"))} }}"
                : "";

            string responseInitialization;
            if (endpoint.ResponseConstructorParameters.Any())
            {
                responseInitialization =
                    $"({string.Join(", ", endpoint.ResponseConstructorParameters.Select(p => $"grpcResponse.{p.SourceProperty}"))})";
            }
            else if (endpoint.ResponsePropertyMaps.Any())
            {
                responseInitialization =
                    $" {{ {string.Join(", ", endpoint.ResponsePropertyMaps.Select(m => $"{m.DestProperty} = grpcResponse.{m.SourceProperty}"))} }}";
            }
            else
            {
                responseInitialization = "()";
            }

            sb.AppendLine(
                $@"
        private async Task<{endpoint.CqrsResponseType}> Handle{endpoint.GrpcMethodName}({endpoint.CqrsRequestType} request, CancellationToken ct)
        {{
            var channel = _channelProvider.GetChannelFor<{endpoint.GrpcClientType}>();
            var client = new {endpoint.GrpcClientType}(channel);

            var grpcRequest = new {endpoint.GrpcRequestType}{requestMappings};

            var headers = _metadataProvider?.GetMetadata();
            var callOptions = new CallOptions(headers: headers, cancellationToken: ct);

            var grpcResponse = await client.{endpoint.GrpcMethodName}(grpcRequest, callOptions);
"
            );
            if (!endpoint.IsVoidCommand)
            {
                sb.AppendLine(
                    $@"
            if (grpcResponse == null)
                throw new RpcException(new Status(StatusCode.NotFound, ""Response data not found.""));

            return new {endpoint.CqrsResponseType.TrimEnd('?')}{responseInitialization};"
                );
            }
            else
            {
                sb.AppendLine("            return default;");
            }
            sb.AppendLine("        }");
        }

        sb.AppendLine(
            @"    }
}"
        );
        return sb.ToString();
    }

    private static string GenerateGrpcClientDI()
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            @"// <auto-generated/> by LinKit.Generator
#nullable enable
using Microsoft.Extensions.DependencyInjection;

namespace LinKit.Core
{
    public static class GrpcClientExtensions
    {
        public static IServiceCollection AddGrpcMediator(this IServiceCollection services)
        {"
        );

        sb.AppendLine(
            $"            services.AddTransient<LinKit.Core.Grpc.IGrpcMediator, LinKit.Generated.Grpc.GrpcClientMediator>();"
        );
        sb.AppendLine(
            @"
            return services;
        }
    }
}
"
        );
        return sb.ToString();
    }
}
