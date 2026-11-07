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
    bool IsVoidCommand,
    bool HasDeadline,
    bool IsResult,
    string? ResultInnerType
);

internal record ConstructorParameterMap(
    string ParameterName,
    string SourceProperty,
    string ParameterType,
    string? TransformTemplate = null
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
        IncrementalGeneratorInitializationContext context,
        IncrementalValueProvider<string> generatedNamespace
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
            .Combine(generatedNamespace)
            .Select(
                (data, _) =>
                {
                    var endpoints = data.Left;
                    var services = new List<GrpcClientServiceInfo>();
                    if (endpoints.Any())
                    {
                        services.Add(
                            new GrpcClientServiceInfo(
                                $"services.AddTransient<LinKit.Grpc.IGrpcMediator, global::{data.Right}.GrpcClientMediator>();"
                            )
                        );
                    }
                    return (IReadOnlyList<GrpcClientServiceInfo>)services;
                }
            );
    }

    public static void Initialize(
        IncrementalGeneratorInitializationContext context,
        IncrementalValueProvider<string> generatedNamespace
    )
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
            validEndpoints.Collect().Combine(generatedNamespace),
            (spc, data) =>
            {
                var endpoints = data.Left;
                if (endpoints.IsEmpty)
                {
                    return;
                }

                var source = GenerateGrpcMediator(endpoints, data.Right);
                spc.AddSource(
                    "LinKit.Grpc.ClientMediator.g.cs",
                    SourceText.From(source, Encoding.UTF8)
                );
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

        var methodCandidates = grpcClientSymbol
            .GetMembers()
            .OfType<IMethodSymbol>()
            .Where(m => m.Parameters.Length >= 1 && m.ReturnType.Name.Contains("AsyncUnaryCall"))
            .ToList();

        var rpcMethod =
            methodCandidates.FirstOrDefault(m =>
                string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase)
            )
            ?? methodCandidates.FirstOrDefault(m =>
                string.Equals(m.Name, methodName + "Async", StringComparison.OrdinalIgnoreCase)
            )
            ?? (
                methodName.EndsWith("Async", StringComparison.OrdinalIgnoreCase)
                    ? methodCandidates.FirstOrDefault(m =>
                        string.Equals(
                            m.Name,
                            methodName.Substring(0, methodName.Length - 5),
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    : null
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

        // A Result<T> response is a wrapper: the gRPC payload maps onto T, and a transport failure becomes a failed
        // result instead of an exception. A plain Result response carries no payload at all.
        var declaredResponseSymbol =
            cqrsInterface.TypeArguments.FirstOrDefault() as INamedTypeSymbol;
        bool isResult = IsLinKitResult(declaredResponseSymbol);
        var resultInnerSymbol = GetResultInner(declaredResponseSymbol);
        var resultInnerType = resultInnerSymbol?.ToDisplayString(
            SymbolDisplayFormat.FullyQualifiedFormat
        );
        var cqrsResponseSymbol = isResult ? resultInnerSymbol : declaredResponseSymbol;
        bool hasDeadline = ImplementsDeadline(cqrsRequestSymbol);

        IReadOnlyList<PropertyMap> responseMaps = new List<PropertyMap>();
        IReadOnlyList<ConstructorParameterMap> responseConstructorParameters =
            new List<ConstructorParameterMap>();
        if (!isVoidCommand && cqrsResponseSymbol != null)
        {
            // Deterministic choice: prefer the widest constructor whose parameters all map,
            // ordered by arity then signature so symbol ordering cannot change the output.
            var constructors = cqrsResponseSymbol
                .GetMembers()
                .OfType<IMethodSymbol>()
                .Where(m => m.MethodKind == MethodKind.Constructor && m.Parameters.Length > 0)
                .OrderByDescending(m => m.Parameters.Length)
                .ThenBy(m => m.ToDisplayString(), StringComparer.Ordinal)
                .ToList();

            var grpcProps = grpcResponseSymbol
                .GetMembers()
                .OfType<IPropertySymbol>()
                .Where(p => !p.IsStatic)
                .ToDictionary(p => p.Name, p => p, StringComparer.OrdinalIgnoreCase);

            foreach (var constructor in constructors)
            {
                var paramMaps = new List<ConstructorParameterMap>();
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
                                ),
                                TransformTemplate: GrpcGeneratorPart.GetTransformTemplate(
                                    grpcProp.Type,
                                    param.Type
                                )
                            )
                        );
                    }
                }

                if (paramMaps.Count == constructor.Parameters.Length)
                {
                    responseConstructorParameters = paramMaps;
                    break;
                }
            }

            if (responseConstructorParameters.Count == 0)
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
                GrpcMethodName: rpcMethod.Name,
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
                IsVoidCommand: isVoidCommand,
                HasDeadline: hasDeadline,
                IsResult: isResult,
                ResultInnerType: resultInnerType
            ),
        };
    }

    private static bool IsLinKitResult(INamedTypeSymbol? symbol) =>
        symbol is { Name: "Result" }
        && symbol.ContainingNamespace?.ToDisplayString() == "LinKit.Core.Results";

    private static INamedTypeSymbol? GetResultInner(INamedTypeSymbol? symbol) =>
        IsLinKitResult(symbol) && symbol is { Arity: 1 }
            ? symbol.TypeArguments[0] as INamedTypeSymbol
            : null;

    private static bool ImplementsDeadline(INamedTypeSymbol requestSymbol) =>
        requestSymbol.AllInterfaces.Any(i =>
            string.Equals(
                i.ToDisplayString(),
                "LinKit.Core.Cqrs.IHasDeadline",
                StringComparison.Ordinal
            )
        );

    private static string GenerateGrpcMediator(
        IReadOnlyList<GrpcClientEndpointInfo> endpoints,
        string generatedNamespace
    )
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

        var distinctClients = endpoints.Select(e => e.GrpcClientType).Distinct().ToList();
        var clientFieldNames = new Dictionary<string, string>();
        int clientIndex = 0;
        foreach (var clientType in distinctClients)
        {
            var rawName = clientType.Split('.').Last();
            var fieldName = "_" + char.ToLowerInvariant(rawName[0]) + rawName.Substring(1);
            if (clientFieldNames.ContainsValue(fieldName))
            {
                fieldName += (++clientIndex).ToString();
            }
            clientFieldNames[clientType] = fieldName;
        }

        sb.AppendLine();
        sb.AppendLine($"namespace {generatedNamespace}");
        sb.AppendLine("{");
        sb.AppendLine(
            @"    public sealed class GrpcClientMediator : IGrpcMediator
    {
        private readonly IGrpcClientFactory _factory;"
        );

        foreach (var kvp in clientFieldNames)
        {
            sb.AppendLine($"        private readonly {kvp.Key} {kvp.Value};");
        }

        sb.AppendLine(
            @"
        public GrpcClientMediator(IGrpcClientFactory factory)
        {
            _factory = factory;"
        );

        foreach (var kvp in clientFieldNames)
        {
            sb.AppendLine(
                $"            {kvp.Value} = new {kvp.Key}(factory.GetCallInvokerFor<{kvp.Key}>());"
            );
        }

        sb.AppendLine("        }");

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
                $"                {cmd.CqrsRequestType} c => {GetHandlerMethodName(cmd, asVoid: true)}(c, cancellationToken),"
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
                $"                {cmd.CqrsRequestType} c => (Task<TResponse>)(object){GetHandlerMethodName(cmd)}(c, cancellationToken),"
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
                $"                {q.CqrsRequestType} q => (Task<TResponse>)(object){GetHandlerMethodName(q)}(q, cancellationToken),"
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
                        m.TransformTemplate != null
                            ? $"{m.DestProperty} = {string.Format(m.TransformTemplate, $"request.{m.SourceProperty}")}"
                            : $"{m.DestProperty} = request.{m.SourceProperty}"
                    )
                );
                requestMappings = $" {{ {assignments} }}";
            }
            else
            {
                requestMappings = "()";
            }

            string responseInitialization;
            if (endpoint.ResponseConstructorParameters.Any())
            {
                var constructorArgs = string.Join(
                    ", ",
                    endpoint.ResponseConstructorParameters.Select(p =>
                        p.TransformTemplate != null
                            ? string.Format(p.TransformTemplate, $"grpcResponse.{p.SourceProperty}")
                            : $"grpcResponse.{p.SourceProperty}"
                    )
                );
                responseInitialization = $"({constructorArgs})";
            }
            else if (endpoint.ResponsePropertyMaps.Any())
            {
                var assignments = string.Join(
                    ", ",
                    endpoint.ResponsePropertyMaps.Select(m =>
                        m.TransformTemplate != null
                            ? $"{m.DestProperty} = {string.Format(m.TransformTemplate, $"grpcResponse.{m.SourceProperty}")}"
                            : $"{m.DestProperty} = grpcResponse.{m.SourceProperty}"
                    )
                );
                responseInitialization = $" {{ {assignments} }}";
            }
            else
            {
                responseInitialization = "()";
            }

            var clientField = clientFieldNames[endpoint.GrpcClientType];
            var deadlineBlock = endpoint.HasDeadline
                ? @"            // The request declares a deadline; carry it so a slow remote cannot outlive the caller's budget.
            System.DateTime? deadline = request is LinKit.Core.Cqrs.IHasDeadline { Deadline: { } relative } && relative > System.TimeSpan.Zero
                ? System.DateTime.UtcNow + relative
                : null;
"
                : @"            System.DateTime? deadline = null;
";

            sb.AppendLine(
                $@"
        private async Task<{endpoint.CqrsResponseType}> {GetHandlerMethodName(endpoint)}({endpoint.CqrsRequestType} request, CancellationToken cancellationToken)
        {{
            var grpcRequest = new {endpoint.GrpcRequestType}{requestMappings};

{deadlineBlock}
            var headers = _factory.GetMetadata();
            var callOptions = new CallOptions(headers: headers, deadline: deadline, cancellationToken: cancellationToken);
"
            );

            if (endpoint.IsResult)
            {
                var failureExpression = endpoint.ResultInnerType is null
                    ? "global::LinKit.Grpc.GrpcResultMapper.ToResult(grpcError)"
                    : $"global::LinKit.Grpc.GrpcResultMapper.ToResult<{endpoint.ResultInnerType}>(grpcError)";

                sb.AppendLine(
                    $@"            {endpoint.GrpcResponseType}? grpcResponse;
            try
            {{
                grpcResponse = await {clientField}.{endpoint.GrpcMethodName}(grpcRequest, callOptions).ConfigureAwait(false);
            }}
            catch (RpcException grpcError) when (!cancellationToken.IsCancellationRequested)
            {{
                return {failureExpression};
            }}
"
                );
            }
            else
            {
                sb.AppendLine(
                    $@"            var grpcResponse = await {clientField}.{endpoint.GrpcMethodName}(grpcRequest, callOptions).ConfigureAwait(false);
"
                );
            }

            if (!endpoint.IsVoidCommand)
            {
                if (endpoint.IsResult)
                {
                    sb.AppendLine(
                        $@"
            if (grpcResponse == null)
                return {endpoint.CqrsResponseType}.NotFound(""gRPC call returned a null response."", ""NotFound"");
"
                    );

                    sb.AppendLine(
                        endpoint.ResultInnerType is null
                            ? $"            return {endpoint.CqrsResponseType}.Success();"
                            : $"            return {endpoint.CqrsResponseType}.Success(new {endpoint.ResultInnerType}{responseInitialization});"
                    );
                }
                else
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
        private async Task {GetHandlerMethodName(endpoint, asVoid: true)}({endpoint.CqrsRequestType} request, CancellationToken cancellationToken)
        {{
            await {GetHandlerMethodName(endpoint)}(request, cancellationToken).ConfigureAwait(false);
        }}"
                );
            }
        }

        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    internal static class GrpcClientActivatorRegistration");
        sb.AppendLine("    {");
        sb.AppendLine("        [System.Runtime.CompilerServices.ModuleInitializer]");
        sb.AppendLine("        internal static void Register()");
        sb.AppendLine("        {");
        foreach (var clientType in distinctClients)
        {
            sb.AppendLine(
                $"            GrpcClientActivator<{clientType}>.Register(invoker => new {clientType}(invoker));"
            );
        }
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    // Two request types can map to the same RPC method name; suffix with the request type to keep handlers unique.
    private static string GetHandlerMethodName(GrpcClientEndpointInfo endpoint, bool asVoid = false)
    {
        var suffix = SanitizeTypeName(endpoint.CqrsRequestType);
        return asVoid
            ? $"Handle{endpoint.GrpcMethodName}_{suffix}AsVoid"
            : $"Handle{endpoint.GrpcMethodName}_{suffix}";
    }

    private static string SanitizeTypeName(string typeName)
    {
        var sb = new StringBuilder(typeName.Length);
        foreach (var c in typeName)
        {
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        }
        return sb.ToString();
    }
}
