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
    IReadOnlyList<string> MarkerInterfaces,
    IReadOnlyList<string> UnboundMarkerInterfaces,
    string HandlerInterface,
    IReadOnlyList<string> SpecificBehaviors
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
    private const string HandlerAttributeName = "LinKit.Core.Cqrs.CqrsHandlerAttribute";
    private const string ContextAttributeName = "LinKit.Core.Cqrs.CqrsContextAttribute";
    private const string BehaviorAttributeName = "LinKit.Core.Cqrs.CqrsBehaviorAttribute";
    private const string ApplyBehaviorAttributeName = "LinKit.Core.Cqrs.ApplyBehaviorAttribute";
    private const string UnitTypeName = "global::LinKit.Core.Cqrs.Unit";

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

    public static IncrementalValueProvider<IReadOnlyList<CqrsServiceInfo>> GetServices(
        IncrementalGeneratorInitializationContext context
    )
    {
        var collectedHandlers = GetCollectedHandlers(context);
        var collectedBehaviors = GetCollectedBehaviors(context);

        return collectedHandlers
            .Combine(collectedBehaviors)
            .Select(
                (source, _) =>
                {
                    var (handlers, behaviors) = source;
                    var services = new List<CqrsServiceInfo>();

                    foreach (var handler in handlers)
                    {
                        services.Add(
                            new CqrsServiceInfo(
                                $"services.AddTransient<{handler.HandlerInterface}, {handler.HandlerType}>();"
                            )
                        );

                        var registeredForThisHandler = new HashSet<string>();

                        // 1. Đăng ký Specific Behaviors (ApplyBehavior)
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

                        // 2. Đăng ký Global Behaviors
                        var applicable = behaviors
                            .Where(b => b != null && IsBehaviorApplicable(b!, handler))
                            .OrderBy(b => b!.Order);
                        foreach (var b in applicable)
                        {
                            string closedType =
                                $"{b!.UnboundBehaviorType}<{handler.RequestType}, {handler.ResponseType}>";
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

    public static void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var collectedHandlers = GetCollectedHandlers(context);
        var collectedBehaviors = GetCollectedBehaviors(context);
        var combined = collectedHandlers.Combine(collectedBehaviors);

        context.RegisterSourceOutput(
            combined,
            (spc, source) =>
            {
                if (!source.Left.Any())
                    return;
                string mediatorSource = GenerateMediatorClass(source.Left, source.Right!);
                spc.AddSource("Cqrs.Mediator.g.cs", SourceText.From(mediatorSource, Encoding.UTF8));
            }
        );
    }

    #endregion

    #region Data Collection (Handlers & Context)

    private static IncrementalValueProvider<IReadOnlyList<HandlerInfo>> GetCollectedHandlers(
        IncrementalGeneratorInitializationContext context
    )
    {
        // Source 1: Từ [CqrsHandler] trên từng class
        var fromAttribute = context.SyntaxProvider.ForAttributeWithMetadataName(
            HandlerAttributeName,
            (n, _) => n is ClassDeclarationSyntax,
            (c, _) => (INamedTypeSymbol)c.TargetSymbol
        );

        // Source 2: Từ [CqrsContext(typeof(A), typeof(B))]
        var fromContext = context
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

                    return arg
                        .Values.Select(v => v.Value as INamedTypeSymbol)
                        .Where(s => s != null)
                        .ToImmutableArray()!;
                }
            )
            .SelectMany((symbols, _) => symbols);

        return fromAttribute
            .Collect()
            .Combine(fromContext.Collect())
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
                        uniqueSymbols.Select(GetHandlerInfo).Where(x => x != null).ToList()!;
                }
            );
    }

    private static IncrementalValueProvider<ImmutableArray<BehaviorInfo?>> GetCollectedBehaviors(
        IncrementalGeneratorInitializationContext context
    )
    {
        return context
            .SyntaxProvider.ForAttributeWithMetadataName(
                BehaviorAttributeName,
                (n, _) => n is ClassDeclarationSyntax,
                (c, _) =>
                {
                    var symbol = (INamedTypeSymbol)c.TargetSymbol;
                    var attr = symbol
                        .GetAttributes()
                        .First(ad =>
                            GetCleanName(ad.AttributeClass?.ToDisplayString() ?? "")
                            == GetCleanName(BehaviorAttributeName)
                        );
                    var targetType = attr.ConstructorArguments[0].Value as INamedTypeSymbol;
                    if (targetType == null)
                        return null;

                    var constraints = new List<string>();
                    var tRequest = symbol.TypeParameters.FirstOrDefault(tp =>
                        tp.Name == "TRequest"
                    );
                    if (tRequest != null)
                        foreach (var ct in tRequest.ConstraintTypes)
                            constraints.Add(
                                ct.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                            );

                    string unboundName = symbol.IsGenericType
                        ? symbol.OriginalDefinition.ToDisplayString(
                            SymbolDisplayFormat.FullyQualifiedFormat
                        )
                        : symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                    return new BehaviorInfo(
                        unboundName.Split('<')[0],
                        (int)(attr.ConstructorArguments[1].Value ?? 0),
                        targetType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                        constraints
                    );
                }
            )
            .Collect();
    }

    private static HandlerInfo? GetHandlerInfo(INamedTypeSymbol classSymbol)
    {
        var handlerInterface = classSymbol.AllInterfaces.FirstOrDefault(i =>
        {
            var name = GetCleanName(
                i.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            );
            return name == ICommandHandlerName || name == IQueryHandlerName;
        });

        if (handlerInterface == null)
            return null;

        var requestType = handlerInterface.TypeArguments[0];
        var responseType =
            handlerInterface.TypeArguments.Length > 1
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

        return new HandlerInfo(
            classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            requestType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            responseType,
            new List<string>(),
            unboundMarkers.Distinct().ToList(),
            handlerInterface.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            specificBehaviors
        );
    }

    #endregion

    #region Source Generation

    private static string GenerateMediatorClass(
        IReadOnlyList<HandlerInfo> handlers,
        IReadOnlyList<BehaviorInfo?> behaviors
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

namespace LinKit.Generated.Cqrs
{
    internal sealed class Mediator : IMediator
    {
        private readonly IServiceProvider _serviceProvider;
        public Mediator(IServiceProvider serviceProvider) => _serviceProvider = serviceProvider;

        public Task SendAsync(ICommand command, CancellationToken cancellationToken = default) => (object)command switch {"
        );

        foreach (
            var h in handlers.Where(x =>
                GetCleanName(x.HandlerInterface) == ICommandHandlerName
                && x.ResponseType == UnitTypeName
            )
        )
            sb.AppendLine(
                $"            {h.RequestType} c => HandleVoidRequest(c, cancellationToken),"
            );

        sb.AppendLine(
            @"            _ => throw new InvalidOperationException()
        };

        public Task<TResponse> SendAsync<TResponse>(ICommand<TResponse> command, CancellationToken cancellationToken = default) => (object)command switch {"
        );

        foreach (
            var h in handlers.Where(x =>
                GetCleanName(x.HandlerInterface) == ICommandHandlerName
                && x.ResponseType != UnitTypeName
            )
        )
            sb.AppendLine(
                $"            {h.RequestType} c => (Task<TResponse>)(object)HandleResultRequest(c, cancellationToken),"
            );

        sb.AppendLine(
            @"            _ => throw new InvalidOperationException()
        };

        public Task<TResponse> QueryAsync<TResponse>(IQuery<TResponse> query, CancellationToken cancellationToken = default) => (object)query switch {"
        );

        foreach (
            var h in handlers.Where(x => GetCleanName(x.HandlerInterface) == IQueryHandlerName)
        )
            sb.AppendLine(
                $"            {h.RequestType} q => (Task<TResponse>)(object)HandleResultRequest(q, cancellationToken),"
            );

        sb.AppendLine(
            @"            _ => throw new InvalidOperationException()
        };"
        );

        foreach (var h in handlers)
        {
            if (h.ResponseType == UnitTypeName)
            {
                sb.AppendLine(
                    $@"
        private async Task HandleVoidRequest({h.RequestType} request, CancellationToken cancellationToken) {{
            Func<Task> next = () => _serviceProvider.GetRequiredService<{h.HandlerInterface}>().HandleAsync(request, cancellationToken);"
                );
                GeneratePipelineLogic(sb, h, behaviors!, false);
                sb.AppendLine("            await next();\n        }");
            }
            else
            {
                sb.AppendLine(
                    $@"
        private Task<{h.ResponseType}> HandleResultRequest({h.RequestType} request, CancellationToken cancellationToken) {{
            RequestHandlerDelegate<{h.ResponseType}> next = () => _serviceProvider.GetRequiredService<{h.HandlerInterface}>().HandleAsync(request, cancellationToken);"
                );
                GeneratePipelineLogic(sb, h, behaviors!, true);
                sb.AppendLine("            return next();\n        }");
            }
        }
        sb.AppendLine("    }\n}");
        return sb.ToString();
    }

    private static void GeneratePipelineLogic(
        StringBuilder sb,
        HandlerInfo handler,
        IReadOnlyList<BehaviorInfo> availableBehaviors,
        bool hasResult
    )
    {
        var global = availableBehaviors
            .Where(b => IsBehaviorApplicable(b, handler))
            .OrderBy(b => b.Order)
            .ToList();

        // Wrap Specific (ApplyBehavior)
        foreach (var sbType in handler.SpecificBehaviors.AsEnumerable().Reverse())
        {
            string closedType =
                $"{GetCleanName(sbType)}<{handler.RequestType}, {handler.ResponseType}>";
            sb.AppendLine(
                $@"            {{
                var capturedNext = next;
                next = {(hasResult ? "" : "async ")}() => {(hasResult ? "" : "await ")}_serviceProvider.GetRequiredService<{closedType}>().HandleAsync(request, {(hasResult ? "capturedNext" : "() => capturedNext().ContinueWith(_ => " + UnitTypeName + ".Value, cancellationToken)")}, cancellationToken);
            }}"
            );
        }

        // Wrap Global
        foreach (var b in global.AsEnumerable().Reverse())
        {
            string closedType =
                $"{b.UnboundBehaviorType}<{handler.RequestType}, {handler.ResponseType}>";
            sb.AppendLine(
                $@"            {{
                var capturedNext = next;
                next = {(hasResult ? "" : "async ")}() => {(hasResult ? "" : "await ")}_serviceProvider.GetRequiredService<{closedType}>().HandleAsync(request, {(hasResult ? "capturedNext" : "() => capturedNext().ContinueWith(_ => " + UnitTypeName + ".Value, cancellationToken)")}, cancellationToken);
            }}"
            );
        }
    }
    #endregion
}
