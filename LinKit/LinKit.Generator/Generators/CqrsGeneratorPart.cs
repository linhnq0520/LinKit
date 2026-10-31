using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace LinKit.Generator.Generators;

internal record HandlerInfo(
    string HandlerType,
    string RequestType,
    string ResponseType,
    Location HandlerLocation,
    IReadOnlyList<string> MarkerInterfaces,
    IReadOnlyList<string> UnboundMarkerInterfaces,
    string HandlerInterface,
    IReadOnlyList<string> SpecificBehaviors,
    bool IsNotification
);

internal record BehaviorInfo(
    string UnboundBehaviorType,
    int Order,
    string TargetInterface,
    IReadOnlyList<string> GenericConstraints
);

internal static class CqrsGeneratorPart
{
    private const string ICommandHandlerName = "LinKit.Core.Cqrs.ICommandHandler";
    private const string IQueryHandlerName = "LinKit.Core.Cqrs.IQueryHandler";
    private const string INotificationHandlerName = "LinKit.Core.Cqrs.INotificationHandler";
    private const string HandlerAttributeName = "LinKit.Core.Cqrs.CqrsHandlerAttribute";
    private const string ContextAttributeName = "LinKit.Core.Cqrs.CqrsContextAttribute";
    private const string BehaviorAttributeName = "LinKit.Core.Cqrs.CqrsBehaviorAttribute";
    private const string ApplyBehaviorAttributeName = "LinKit.Core.Cqrs.ApplyBehaviorAttribute";
    private const string UnitTypeName = "global::LinKit.Core.Cqrs.Unit";
    private static readonly DiagnosticDescriptor DuplicateHandler = new(
        id: "LNKCQRS001",
        title: "Duplicate request handler",
        messageFormat: "Request '{0}' has multiple handlers: '{1}' and '{2}'. Commands and queries must have exactly one handler.",
        category: "CQRS",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    private static string GetCleanName(string fullName)
    {
        if (string.IsNullOrEmpty(fullName))
            return fullName;
        string name = fullName;
        if (name.StartsWith("global::"))
            name = name.Substring(8);
        int angleIndex = name.IndexOf('<');
        return angleIndex > 0 ? name.Substring(0, angleIndex) : name;
    }

    private static bool IsBehaviorApplicable(BehaviorInfo behavior, HandlerInfo handler)
    {
        string cleanTarget = GetCleanName(behavior.TargetInterface);
        bool targetMatch =
            cleanTarget == "System.Object" || handler.UnboundMarkerInterfaces.Contains(cleanTarget);

        if (!targetMatch)
            return false;

        return behavior.GenericConstraints.All(constraint =>
        {
            string cleanConstraint = GetCleanName(constraint);
            return handler.UnboundMarkerInterfaces.Contains(cleanConstraint);
        });
    }

    #region Pipeline & Services Setup

    public static IncrementalValueProvider<(IReadOnlyList<HandlerInfo> Handlers, IReadOnlyList<BehaviorInfo> Behaviors)> CreatePipeline(
        IncrementalGeneratorInitializationContext context
    )
    {
        var scannedClassesFromContext = GetScannedClassesFromContext(context);
        var collectedHandlers = GetCollectedHandlers(context, scannedClassesFromContext);
        var collectedBehaviors = GetCollectedBehaviors(context, scannedClassesFromContext);
        return collectedHandlers.Combine(collectedBehaviors);
    }

    public static IncrementalValueProvider<IReadOnlyList<CqrsServiceInfo>> GetServices(
        IncrementalValueProvider<(IReadOnlyList<HandlerInfo> Handlers, IReadOnlyList<BehaviorInfo> Behaviors)> combined,
        IncrementalValueProvider<string> generatedNamespace
    )
    {
        return combined.Combine(generatedNamespace).Select(
            (data, _) =>
            {
                var source = data.Left;
                var (handlers, behaviors) = source;
                var services = new List<CqrsServiceInfo>();

                foreach (var handler in handlers)
                {
                    services.Add(
                        new CqrsServiceInfo(
                            $"services.AddTransient<{handler.HandlerInterface}, {handler.HandlerType}>();"
                        )
                    );

                    if (handler.IsNotification)
                        continue;

                    var registeredForThisHandler = new HashSet<string>();

                    // 1. Specific Behaviors
                    foreach (var sbType in handler.SpecificBehaviors)
                    {
                        string closedType =
                            $"{GetCleanName(sbType)}<{handler.RequestType}, {handler.ResponseType}>";
                        if (registeredForThisHandler.Add(closedType))
                            services.Add(
                                new CqrsServiceInfo(
                                    $"services.AddTransient<global::{closedType}>();"
                                )
                            );
                    }

                    // 2. Global Behaviors
                    var applicable = behaviors
                        .Where(b => IsBehaviorApplicable(b, handler))
                        .OrderBy(b => b.Order);

                    foreach (var b in applicable)
                    {
                        string closedType =
                            $"{b.UnboundBehaviorType}<{handler.RequestType}, {handler.ResponseType}>";
                        if (registeredForThisHandler.Add(closedType))
                            services.Add(
                                new CqrsServiceInfo($"services.AddTransient<{closedType}>();")
                            );
                    }
                }
                return (IReadOnlyList<CqrsServiceInfo>)services;
            }
        );
    }

    public static void Initialize(
        IncrementalGeneratorInitializationContext context,
        IncrementalValueProvider<(IReadOnlyList<HandlerInfo> Handlers, IReadOnlyList<BehaviorInfo> Behaviors)> combined,
        IncrementalValueProvider<string> generatedNamespace
    )
    {
        context.RegisterSourceOutput(
            combined.Combine(generatedNamespace),
            (spc, source) =>
            {
                if (!source.Left.Handlers.Any())
                    return;
                ReportDuplicateHandlers(spc, source.Left.Handlers);
                IReadOnlyList<HandlerInfo> dispatchHandlers = GetUniqueDispatchHandlers(
                    source.Left.Handlers
                );
                string mediatorSource = GenerateMediatorClass(dispatchHandlers, source.Left.Behaviors, source.Right);
                spc.AddSource("LinKit.Core.Cqrs.Mediator.g.cs", SourceText.From(mediatorSource, Encoding.UTF8));
            }
        );
    }

    #endregion

    #region Data Collection (Handlers, Behaviors & Context)

    // [MỚI] Luồng quét Assembly dùng chung cho cả Handler và Behavior
    private static IncrementalValuesProvider<INamedTypeSymbol> GetScannedClassesFromContext(
        IncrementalGeneratorInitializationContext context
    )
    {
        return context
            .SyntaxProvider.ForAttributeWithMetadataName(
                ContextAttributeName,
                (n, _) => n is ClassDeclarationSyntax,
                (c, _) =>
                {
                    var attr = c.Attributes.FirstOrDefault(a =>
                        GetCleanName(a.AttributeClass?.ToDisplayString() ?? "")
                        == GetCleanName(ContextAttributeName)
                    );
                    if (attr == null || attr.ConstructorArguments.Length == 0)
                        return ImmutableArray<INamedTypeSymbol>.Empty;

                    var arg = attr.ConstructorArguments[0];
                    if (arg.Kind != TypedConstantKind.Array)
                        return ImmutableArray<INamedTypeSymbol>.Empty;

                    var markerSymbols = arg
                        .Values.Select(v => v.Value as INamedTypeSymbol)
                        .Where(s => s != null);
                    var scannedTypes = new HashSet<INamedTypeSymbol>(
                        SymbolEqualityComparer.Default
                    );

                    foreach (var marker in markerSymbols)
                    {
                        if (marker?.ContainingAssembly != null)
                        {
                            CollectClassTypes(
                                marker.ContainingAssembly.GlobalNamespace,
                                scannedTypes
                            );
                        }
                    }
                    return scannedTypes.ToImmutableArray();
                }
            )
            .SelectMany((symbols, _) => symbols);
    }

    private static void CollectClassTypes(
        INamespaceSymbol namespaceSymbol,
        HashSet<INamedTypeSymbol> scannedTypes
    )
    {
        foreach (var type in namespaceSymbol.GetTypeMembers())
        {
            if (
                type.TypeKind == TypeKind.Class
                && !type.IsAbstract
                && type.DeclaredAccessibility == Accessibility.Public
            )
            {
                scannedTypes.Add(type);
            }
        }
        foreach (var childNamespace in namespaceSymbol.GetNamespaceMembers())
        {
            CollectClassTypes(childNamespace, scannedTypes);
        }
    }

    private static IncrementalValueProvider<IReadOnlyList<HandlerInfo>> GetCollectedHandlers(
        IncrementalGeneratorInitializationContext context,
        IncrementalValuesProvider<INamedTypeSymbol> scannedClassesFromContext
    )
    {
        var localHandlers = context.SyntaxProvider.ForAttributeWithMetadataName(
            HandlerAttributeName,
            (n, _) => n is ClassDeclarationSyntax,
            (c, _) => (INamedTypeSymbol)c.TargetSymbol
        );

        return localHandlers
            .Collect()
            .Combine(scannedClassesFromContext.Collect())
            .Select(
                (tuple, _) =>
                {
                    var uniqueSymbols = new HashSet<INamedTypeSymbol>(
                        SymbolEqualityComparer.Default
                    );
                    foreach (var s in tuple.Left)
                        uniqueSymbols.Add(s);
                    foreach (var s in tuple.Right)
                        uniqueSymbols.Add(s);

                    return (IReadOnlyList<HandlerInfo>)
                        uniqueSymbols.SelectMany(GetHandlerInfos).ToList();
                }
            );
    }

    private static IncrementalValueProvider<IReadOnlyList<BehaviorInfo>> GetCollectedBehaviors(
        IncrementalGeneratorInitializationContext context,
        IncrementalValuesProvider<INamedTypeSymbol> scannedClassesFromContext
    )
    {
        var localBehaviors = context.SyntaxProvider.ForAttributeWithMetadataName(
            BehaviorAttributeName,
            (n, _) => n is ClassDeclarationSyntax,
            (c, _) => (INamedTypeSymbol)c.TargetSymbol
        );

        return localBehaviors
            .Collect()
            .Combine(scannedClassesFromContext.Collect())
            .Select(
                (tuple, _) =>
                {
                    var uniqueSymbols = new HashSet<INamedTypeSymbol>(
                        SymbolEqualityComparer.Default
                    );
                    foreach (var s in tuple.Left)
                        uniqueSymbols.Add(s);
                    foreach (var s in tuple.Right)
                        uniqueSymbols.Add(s);

                    // [MỚI] Dùng hàm GetBehaviorInfo để bóc tách attribute, tự động bỏ qua các class không phải Behavior
                    return (IReadOnlyList<BehaviorInfo>)
                        uniqueSymbols.Select(GetBehaviorInfo).Where(x => x != null).ToList()!;
                }
            );
    }

    // [MỚI] Hàm kiểm tra và lấy thông tin Behavior từ bất kỳ Class nào (từ Local hoặc từ quét Assembly)
    private static BehaviorInfo? GetBehaviorInfo(INamedTypeSymbol symbol)
    {
        var attr = symbol
            .GetAttributes()
            .FirstOrDefault(ad =>
                GetCleanName(ad.AttributeClass?.ToDisplayString() ?? "")
                == GetCleanName(BehaviorAttributeName)
            );

        if (attr == null || attr.ConstructorArguments.Length == 0)
            return null;

        var targetType = attr.ConstructorArguments[0].Value as INamedTypeSymbol;
        if (targetType == null)
            return null;

        int order = 0;
        if (attr.ConstructorArguments.Length > 1 && attr.ConstructorArguments[1].Value is int o) { order = o; }
        else if (attr.NamedArguments.FirstOrDefault(kvp => kvp.Key == "Order").Value.Value is int namedOrder) { order = namedOrder; }


        var constraints = new List<string>();
        var tRequest = symbol.TypeParameters.FirstOrDefault(tp => tp.Name == "TRequest");
        if (tRequest != null)
        {
            foreach (var ct in tRequest.ConstraintTypes)
            {
                constraints.Add(ct.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
            }
        }

        string unboundName = symbol.IsGenericType
            ? symbol.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            : symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        return new BehaviorInfo(
            unboundName.Split('<')[0],
            order,
            targetType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            constraints
        );
    }

    // Một class có thể implement nhiều handler interface (vd. nhiều INotificationHandler<T>);
    // sinh một descriptor cho mỗi interface thay vì chỉ lấy interface đầu tiên.
    private static IEnumerable<HandlerInfo> GetHandlerInfos(INamedTypeSymbol classSymbol)
    {
        foreach (var handlerInterface in classSymbol.AllInterfaces)
        {
            var name = GetCleanName(
                handlerInterface.OriginalDefinition.ToDisplayString(
                    SymbolDisplayFormat.FullyQualifiedFormat
                )
            );
            bool isNotification = name == INotificationHandlerName;
            if (!isNotification && name != ICommandHandlerName && name != IQueryHandlerName)
                continue;

            yield return BuildHandlerInfo(classSymbol, handlerInterface, isNotification);
        }
    }

    private static HandlerInfo BuildHandlerInfo(
        INamedTypeSymbol classSymbol,
        INamedTypeSymbol handlerInterface,
        bool isNotification
    )
    {
        var requestType = handlerInterface.TypeArguments[0];

        var responseType =
            (!isNotification && handlerInterface.TypeArguments.Length > 1)
                ? handlerInterface
                    .TypeArguments[1]
                    .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                : UnitTypeName;

        var unboundMarkers = new List<string>();
        void AddMarkers(ITypeSymbol s)
        {
            unboundMarkers.Add(
                GetCleanName(s.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
            );
            foreach (var i in s.AllInterfaces)
            {
                unboundMarkers.Add(
                    GetCleanName(i.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
                );
                unboundMarkers.Add(
                    GetCleanName(
                        i.OriginalDefinition.ToDisplayString(
                            SymbolDisplayFormat.FullyQualifiedFormat
                        )
                    )
                );
            }
        }
        AddMarkers(requestType);

        var specificBehaviors = new List<string>();

        if (!isNotification)
        {
            var applyAttrs = requestType
                .GetAttributes()
                .Where(ad =>
                    GetCleanName(ad.AttributeClass?.ToDisplayString() ?? "")
                    == GetCleanName(ApplyBehaviorAttributeName)
                );
            foreach (var attr in applyAttrs)
            {
                if (
                    attr.ConstructorArguments.Length > 0
                    && attr.ConstructorArguments[0].Kind == TypedConstantKind.Array
                )
                {
                    foreach (var val in attr.ConstructorArguments[0].Values)
                        if (val.Value is INamedTypeSymbol bSymbol)
                            specificBehaviors.Add(
                                bSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                            );
                }
            }
        }

        return new HandlerInfo(
            classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            requestType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            responseType,
            classSymbol.Locations.FirstOrDefault(location => location.IsInSource) ?? Location.None,
            new List<string>(),
            unboundMarkers.Distinct().ToList(),
            handlerInterface.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            specificBehaviors,
            isNotification
        );
    }

    private static IEnumerable<IGrouping<(string RequestType, string HandlerKind), HandlerInfo>>
        GetDispatchHandlerGroups(IEnumerable<HandlerInfo> handlers) =>
        handlers
            .Where(handler => !handler.IsNotification)
            .GroupBy(handler =>
                (
                    handler.RequestType,
                    GetCleanName(handler.HandlerInterface).StartsWith(
                        ICommandHandlerName,
                        System.StringComparison.Ordinal
                    )
                        ? ICommandHandlerName
                        : IQueryHandlerName
                )
            );

    private static void ReportDuplicateHandlers(
        SourceProductionContext context,
        IReadOnlyList<HandlerInfo> handlers
    )
    {
        foreach (var group in GetDispatchHandlerGroups(handlers))
        {
            HandlerInfo[] ordered = group.OrderBy(handler => handler.HandlerType).ToArray();
            for (int i = 1; i < ordered.Length; i++)
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        DuplicateHandler,
                        ordered[i].HandlerLocation,
                        ordered[i].RequestType,
                        ordered[0].HandlerType,
                        ordered[i].HandlerType
                    )
                );
            }
        }
    }

    private static IReadOnlyList<HandlerInfo> GetUniqueDispatchHandlers(
        IReadOnlyList<HandlerInfo> handlers
    )
    {
        var duplicateDispatchHandlers = new HashSet<HandlerInfo>(
            GetDispatchHandlerGroups(handlers)
                .SelectMany(group =>
                    group.OrderBy(handler => handler.HandlerType).Skip(1)
                )
        );
        return handlers.Where(handler => !duplicateDispatchHandlers.Contains(handler)).ToList();
    }

    #endregion

    #region Source Generation

    private static string GenerateMediatorClass(
        IReadOnlyList<HandlerInfo> handlers,
        IReadOnlyList<BehaviorInfo?> behaviors,
        string generatedNamespace
    )
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine(
            @"// <auto-generated />
#nullable enable
using LinKit.Core.Cqrs;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Threading;
using System.Threading.Tasks;

"
        );
        sb.AppendLine($"namespace {generatedNamespace}");
        sb.AppendLine("{");
        sb.AppendLine(
            @"    internal sealed class Mediator : IMediator
    {
        private readonly IServiceProvider _serviceProvider;
        public Mediator(IServiceProvider serviceProvider) => _serviceProvider = serviceProvider;

        public Task SendAsync(ICommand command, CancellationToken cancellationToken = default) => (object)command! switch {"
        );

        foreach (
            var h in handlers.Where(x =>
                !x.IsNotification
                && GetCleanName(x.HandlerInterface) == ICommandHandlerName
                && x.ResponseType == UnitTypeName
            )
        )
        {
            sb.AppendLine(
                $"            {h.RequestType} c => HandleVoidRequest(c, cancellationToken),"
            );
        }

        sb.AppendLine(
            @"            _ => throw new HandlerNotFoundException(command.GetType())
        };

        public Task<TResponse> SendAsync<TResponse>(ICommand<TResponse> command, CancellationToken cancellationToken = default) => (object)command! switch {"
        );

        foreach (
            var h in handlers.Where(x =>
                !x.IsNotification
                && GetCleanName(x.HandlerInterface) == ICommandHandlerName
                && x.ResponseType != UnitTypeName
            )
        )
        {
            sb.AppendLine(
                $"            {h.RequestType} c => (Task<TResponse>)(object)HandleResultRequest(c, cancellationToken),"
            );
        }

        sb.AppendLine(
            @"            _ => throw new HandlerNotFoundException(command.GetType())
        };

        public Task<TResponse> QueryAsync<TResponse>(IQuery<TResponse> query, CancellationToken cancellationToken = default) => (object)query! switch {"
        );

        foreach (
            var h in handlers.Where(x =>
                !x.IsNotification && GetCleanName(x.HandlerInterface) == IQueryHandlerName
            )
        )
        {
            sb.AppendLine(
                $"            {h.RequestType} q => (Task<TResponse>)(object)HandleResultRequest(q, cancellationToken),"
            );
        }

        sb.AppendLine(
            @"            _ => throw new HandlerNotFoundException(query.GetType())
        };

        public Task PublishAsync<TNotification>(TNotification notification, PublishStrategy strategy = PublishStrategy.Sequential, CancellationToken ct = default) => (object)notification! switch {"
        );

        var notificationGroups = handlers
            .Where(h => h.IsNotification)
            .GroupBy(h => h.RequestType)
            .ToList();
        foreach (var group in notificationGroups)
        {
            string safeName = group
                .Key.Replace(".", "_")
                .Replace("<", "_")
                .Replace(">", "_")
                .Replace("global::", "");
            sb.AppendLine($"            {group.Key} n => Publish_{safeName}(n, strategy, ct),");
        }

        sb.AppendLine(
            @"            _ => Task.CompletedTask
        };"
        );

        // Gen logic Command/Query Handler Private methods
        foreach (var h in handlers.Where(h => !h.IsNotification))
        {
            var applicableBehaviors = GetApplicableBehaviorTypes(h, behaviors!);

            if (h.ResponseType == UnitTypeName)
            {
                if (applicableBehaviors.Count == 0)
                {
                    // Zero-allocation Fast-path: Direct call without delegate/closure or async state machine
                    sb.AppendLine(
                        $@"
        private Task HandleVoidRequest({h.RequestType} request, CancellationToken cancellationToken)
            => _serviceProvider.GetRequiredService<{h.HandlerInterface}>().HandleAsync(request, cancellationToken);"
                    );
                }
                else
                {
                    sb.AppendLine(
                        $@"
        private async Task HandleVoidRequest({h.RequestType} request, CancellationToken cancellationToken)
        {{
            Func<Task> next = () => _serviceProvider.GetRequiredService<{h.HandlerInterface}>().HandleAsync(request, cancellationToken);"
                    );
                    foreach (var closedType in applicableBehaviors)
                    {
                        AppendBehaviorWrap(sb, closedType, false);
                    }
                    sb.AppendLine("            await next();\n        }");
                }
            }
            else
            {
                if (applicableBehaviors.Count == 0)
                {
                    // Zero-allocation Fast-path: Direct call without delegate/closure or async state machine
                    sb.AppendLine(
                        $@"
        private Task<{h.ResponseType}> HandleResultRequest({h.RequestType} request, CancellationToken cancellationToken)
            => _serviceProvider.GetRequiredService<{h.HandlerInterface}>().HandleAsync(request, cancellationToken);"
                    );
                }
                else
                {
                    sb.AppendLine(
                        $@"
        private Task<{h.ResponseType}> HandleResultRequest({h.RequestType} request, CancellationToken cancellationToken)
        {{
            RequestHandlerDelegate<{h.ResponseType}> next = () => _serviceProvider.GetRequiredService<{h.HandlerInterface}>().HandleAsync(request, cancellationToken);"
                    );
                    foreach (var closedType in applicableBehaviors)
                    {
                        AppendBehaviorWrap(sb, closedType, true);
                    }
                    sb.AppendLine("            return next();\n        }");
                }
            }
        }

        // Gen logic Notification Publisher Private methods
        foreach (var group in notificationGroups)
        {
            string safeName = group
                .Key.Replace(".", "_")
                .Replace("<", "_")
                .Replace(">", "_")
                .Replace("global::", "");
            string handlerInterface = group.First().HandlerInterface; // INotificationHandler<T>

            sb.AppendLine(
                $@"
        private async Task Publish_{safeName}({group.Key} notification, PublishStrategy strategy, CancellationToken ct)
        {{
            var handlers = _serviceProvider.GetServices<{handlerInterface}>();

            if (strategy == PublishStrategy.Sequential)
            {{
                foreach (var handler in handlers)
                {{
                    await handler.HandleAsync(notification, ct).ConfigureAwait(false);
                }}
            }}
            else
            {{
                var tasks = new System.Collections.Generic.List<Task>();
                foreach (var handler in handlers)
                {{
                    tasks.Add(handler.HandleAsync(notification, ct));
                }}
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }}
        }}"
            );
        }

        sb.AppendLine("    }\n}");
        return sb.ToString();
    }

    private static List<string> GetApplicableBehaviorTypes(
        HandlerInfo handler,
        IReadOnlyList<BehaviorInfo> availableBehaviors
    )
    {
        var globalBehaviors = availableBehaviors
            .Where(b => IsBehaviorApplicable(b, handler))
            .OrderBy(b => b.Order)
            .ToList();

        string requestType = handler.RequestType;
        string responseType = handler.ResponseType;

        // Specific behaviors trước, global behaviors sau — cả 2 đều reverse
        var specificClosedTypes = handler.SpecificBehaviors.Select(sbType =>
            $"{GetCleanName(sbType)}<{requestType}, {responseType}>"
        );

        var globalClosedTypes = globalBehaviors.Select(b =>
            $"{b.UnboundBehaviorType}<{requestType}, {responseType}>"
        );

        // Specific wrap trước (reverse), global wrap sau (reverse)
        return specificClosedTypes.Reverse().Concat(globalClosedTypes.Reverse()).ToList();
    }

    private static void AppendBehaviorWrap(StringBuilder sb, string closedType, bool hasResult)
    {
        string asyncModifier = hasResult ? "" : "async ";
        string awaitModifier = hasResult ? "" : "await ";
        string nextDelegate = hasResult
            ? "capturedNext"
            : $"async () => {{ await capturedNext(); return {UnitTypeName}.Value; }}";

        sb.AppendLine(
            $$"""
                        {
                            var capturedNext = next;
                            next = {{asyncModifier}}() => {{awaitModifier}}_serviceProvider
                                .GetRequiredService<{{closedType}}>()
                                .HandleAsync(
                                    request,
                                    {{nextDelegate}},
                                    cancellationToken);
                        }
            """
        );
    }
    #endregion
}
