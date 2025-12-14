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
    private const string UnitTypeName = "LinKit.Core.Cqrs.Unit";

    #region Pipeline Setup (Initialize and GetServices)

    public static IncrementalValueProvider<IReadOnlyList<CqrsServiceInfo>> GetServices(
        IncrementalGeneratorInitializationContext context
    )
    {
        IncrementalValueProvider<IReadOnlyList<HandlerInfo>> collectedHandlers =
            GetCollectedHandlers(context);
        IncrementalValueProvider<ImmutableArray<BehaviorInfo?>> collectedBehaviors =
            GetCollectedBehaviors(context);

        return collectedHandlers
            .Combine(collectedBehaviors)
            .Select(
                (source, _) =>
                {
                    IReadOnlyList<HandlerInfo> handlers = source.Left;
                    ImmutableArray<BehaviorInfo?> behaviors = source.Right;
                    List<CqrsServiceInfo> services = new List<CqrsServiceInfo>();

                    if (!handlers.Any())
                    {
                        return (IReadOnlyList<CqrsServiceInfo>)services;
                    }

                    foreach (HandlerInfo? handler in handlers)
                    {
                        services.Add(
                            new CqrsServiceInfo(
                                $"services.AddTransient<{handler.HandlerInterface}, {handler.HandlerType}>();"
                            )
                        );
                    }

                    if (behaviors.Any())
                    {
                        HashSet<string> registeredBehaviors = new HashSet<string>();

                        foreach (HandlerInfo? handler in handlers)
                        {
                            IEnumerable<BehaviorInfo?> applicableContractBehaviors =
                                behaviors.Where(b =>
                                {
                                    bool targetMatch =
                                        b.TargetInterface is null
                                        || b.TargetInterface == "global::System.Object"
                                        || handler.MarkerInterfaces.Contains(b.TargetInterface);
                                    if (!targetMatch)
                                    {
                                        return false;
                                    }

                                    bool constraintsMatch = b.GenericConstraints.All(constraint =>
                                        handler.UnboundMarkerInterfaces.Contains(constraint)
                                        || handler.MarkerInterfaces.Contains(constraint)
                                    );
                                    return constraintsMatch;
                                });

                            IEnumerable<string> allApplicableBehaviors = handler
                                .SpecificBehaviors.Select(sb => sb.Split('<')[0])
                                .Concat(
                                    applicableContractBehaviors.Select(cb => cb.UnboundBehaviorType)
                                )
                                .Distinct();

                            foreach (string? unboundBehaviorType in allApplicableBehaviors)
                            {
                                string closedBehaviorType =
                                    $"{unboundBehaviorType}<{handler.RequestType}, {handler.ResponseType}>";

                                if (registeredBehaviors.Add(closedBehaviorType))
                                {
                                    services.Add(
                                        new CqrsServiceInfo(
                                            $"services.AddTransient<{closedBehaviorType}>();"
                                        )
                                    );
                                }
                            }
                        }
                    }

                    return (IReadOnlyList<CqrsServiceInfo>)services;
                }
            );
    }

    public static void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValueProvider<IReadOnlyList<HandlerInfo>> collectedHandlers =
            GetCollectedHandlers(context);
        IncrementalValueProvider<ImmutableArray<BehaviorInfo?>> collectedBehaviors =
            GetCollectedBehaviors(context);

        IncrementalValueProvider<(
            IReadOnlyList<HandlerInfo> Left,
            ImmutableArray<BehaviorInfo?> Right
        )> combined = collectedHandlers.Combine(collectedBehaviors);

        context.RegisterSourceOutput(
            combined,
            (spc, source) =>
            {
                IReadOnlyList<HandlerInfo> handlers = source.Left;
                ImmutableArray<BehaviorInfo?> behaviors = source.Right;
                if (!handlers.Any())
                {
                    return;
                }

                string mediatorSource = GenerateMediatorClass(handlers, behaviors);
                spc.AddSource("Cqrs.Mediator.g.cs", SourceText.From(mediatorSource, Encoding.UTF8));
            }
        );
    }

    #endregion

    #region Data Collection Logic (GetHandlerInfo, etc.)

    private static IncrementalValueProvider<IReadOnlyList<HandlerInfo>> GetCollectedHandlers(
        IncrementalGeneratorInitializationContext context
    )
    {
        IncrementalValuesProvider<INamedTypeSymbol> handlersFromAttribute = context
            .SyntaxProvider.ForAttributeWithMetadataName(
                HandlerAttributeName,
                (n, _) => n is ClassDeclarationSyntax,
                (c, _) => (INamedTypeSymbol)c.TargetSymbol
            )
            .Where(s => s is not null);

        IncrementalValuesProvider<INamedTypeSymbol> handlersFromContext = context
            .SyntaxProvider.ForAttributeWithMetadataName(
                ContextAttributeName,
                (n, _) => n is ClassDeclarationSyntax,
                (c, _) => c
            )
            .SelectMany(
                (data, _) =>
                {
                    INamedTypeSymbol contextSymbol = (INamedTypeSymbol)data.TargetSymbol;
                    List<INamedTypeSymbol> handlers = new List<INamedTypeSymbol>();
                    AttributeData? attributeData = contextSymbol
                        .GetAttributes()
                        .FirstOrDefault(ad =>
                            ad.AttributeClass?.ToDisplayString() == ContextAttributeName
                        );
                    if (attributeData is null || attributeData.ConstructorArguments.Length == 0)
                    {
                        return ImmutableArray<INamedTypeSymbol>.Empty;
                    }

                    TypedConstant constructorArgs = attributeData.ConstructorArguments[0];
                    if (constructorArgs.Kind != TypedConstantKind.Array)
                    {
                        return ImmutableArray<INamedTypeSymbol>.Empty;
                    }

                    foreach (TypedConstant typeConstant in constructorArgs.Values)
                    {
                        if (typeConstant.Value is INamedTypeSymbol handlerTypeSymbol)
                        {
                            handlers.Add(handlerTypeSymbol);
                        }
                    }
                    return handlers.ToImmutableArray();
                }
            );

        IncrementalValueProvider<(
            ImmutableArray<INamedTypeSymbol> Left,
            ImmutableArray<INamedTypeSymbol> Right
        )> allHandlerSymbols = handlersFromAttribute
            .Collect()
            .Combine(handlersFromContext.Collect());

        return allHandlerSymbols.Select(
            (tuple, _) =>
            {
                HashSet<INamedTypeSymbol> uniqueHandlers = new HashSet<INamedTypeSymbol>(
                    SymbolEqualityComparer.Default
                );
                foreach (INamedTypeSymbol? handler in tuple.Left)
                {
                    uniqueHandlers.Add(handler);
                }

                foreach (INamedTypeSymbol? handler in tuple.Right)
                {
                    uniqueHandlers.Add(handler);
                }

                return (IReadOnlyList<HandlerInfo>)
                    uniqueHandlers.Select(GetHandlerInfo).Where(info => info is not null)!.ToList();
            }
        );
    }

    private static IncrementalValueProvider<ImmutableArray<BehaviorInfo?>> GetCollectedBehaviors(
        IncrementalGeneratorInitializationContext context
    )
    {
        return context
            .SyntaxProvider.ForAttributeWithMetadataName(
                fullyQualifiedMetadataName: BehaviorAttributeName,
                predicate: (n, _) => n is ClassDeclarationSyntax,
                transform: (c, _) =>
                {
                    INamedTypeSymbol symbol = (INamedTypeSymbol)c.TargetSymbol;
                    AttributeData attributeData = symbol
                        .GetAttributes()
                        .First(ad => ad.AttributeClass?.ToDisplayString() == BehaviorAttributeName);
                    if (attributeData.ConstructorArguments.Length == 0)
                    {
                        return null;
                    }

                    if (
                        attributeData
                            .ConstructorArguments.FirstOrDefault(arg =>
                                arg.Type?.ToDisplayString() == "System.Type"
                            )
                            .Value
                        is not INamedTypeSymbol targetInterfaceType
                    )
                    {
                        return null;
                    }
                    TypedConstant orderArg = attributeData.ConstructorArguments.FirstOrDefault(
                        arg => arg.Type?.ToDisplayString() == "int"
                    );
                    int order = orderArg.IsNull ? 0 : (int)orderArg.Value!;

                    List<string> constraints = new List<string>();
                    if (symbol.IsGenericType)
                    {
                        ITypeParameterSymbol? typeParameter = symbol.TypeParameters.FirstOrDefault(
                            tp => tp.Name == "TRequest"
                        );
                        if (typeParameter != null)
                        {
                            foreach (
                                ITypeSymbol constraintTypeSymbol in typeParameter.ConstraintTypes
                            )
                            {
                                if (
                                    constraintTypeSymbol is INamedTypeSymbol namedConstraint
                                    && namedConstraint.IsGenericType
                                )
                                {
                                    INamedTypeSymbol originalDefinition =
                                        namedConstraint.OriginalDefinition;
                                    constraints.Add(
                                        originalDefinition.ToDisplayString(
                                            SymbolDisplayFormat.FullyQualifiedFormat
                                        )
                                    );
                                }
                                else
                                {
                                    constraints.Add(
                                        constraintTypeSymbol.ToDisplayString(
                                            SymbolDisplayFormat.FullyQualifiedFormat
                                        )
                                    );
                                }
                            }
                        }
                    }

                    INamedTypeSymbol originalSymbol = symbol.IsGenericType
                        ? symbol.OriginalDefinition
                        : symbol;
                    string unboundTypeName = originalSymbol
                        .ToDisplayString(
                            SymbolDisplayFormat.FullyQualifiedFormat.WithGlobalNamespaceStyle(
                                SymbolDisplayGlobalNamespaceStyle.Included
                            )
                        )
                        .Split('<')[0];

                    return new BehaviorInfo(
                        unboundTypeName,
                        order,
                        targetInterfaceType.ToDisplayString(
                            SymbolDisplayFormat.FullyQualifiedFormat
                        ),
                        constraints
                    );
                }
            )
            .Where(info => info is not null)!
            .Collect();
    }

    private static HandlerInfo? GetHandlerInfo(INamedTypeSymbol classSymbol)
    {
        INamedTypeSymbol? handlerInterface = classSymbol.AllInterfaces.FirstOrDefault(i =>
            i.OriginalDefinition.ToDisplayString().StartsWith(ICommandHandlerName)
            || i.OriginalDefinition.ToDisplayString().StartsWith(IQueryHandlerName)
        );

        if (handlerInterface is null || handlerInterface.TypeArguments.Length == 0)
        {
            return null;
        }

        ITypeSymbol requestTypeSymbol = handlerInterface.TypeArguments[0];
        ITypeSymbol? responseTypeSymbol =
            handlerInterface.TypeArguments.Length > 1 ? handlerInterface.TypeArguments[1] : null;
        string responseTypeName =
            responseTypeSymbol?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            ?? UnitTypeName;
        List<string> markerInterfaces = new List<string>();
        List<string> unboundMarkerInterfaces = new List<string>();

        markerInterfaces.Add(
            requestTypeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
        );

        foreach (INamedTypeSymbol implementedInterface in requestTypeSymbol.AllInterfaces)
        {
            markerInterfaces.Add(
                implementedInterface.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            );

            if (implementedInterface.IsGenericType)
            {
                unboundMarkerInterfaces.Add(
                    implementedInterface.OriginalDefinition.ToDisplayString(
                        SymbolDisplayFormat.FullyQualifiedFormat
                    )
                );
            }
        }

        List<string> specificBehaviors = new List<string>();
        IEnumerable<AttributeData> applyBehaviorAttributes = requestTypeSymbol
            .GetAttributes()
            .Where(ad => ad.AttributeClass?.ToDisplayString() == ApplyBehaviorAttributeName);
        foreach (AttributeData? attr in applyBehaviorAttributes)
        {
            if (
                attr.ConstructorArguments.Length > 0
                && attr.ConstructorArguments[0].Kind == TypedConstantKind.Array
            )
            {
                foreach (TypedConstant typeConstant in attr.ConstructorArguments[0].Values)
                {
                    if (typeConstant.Value is INamedTypeSymbol behaviorTypeSymbol)
                    {
                        specificBehaviors.Add(
                            behaviorTypeSymbol.ToDisplayString(
                                SymbolDisplayFormat.FullyQualifiedFormat
                            )
                        );
                    }
                }
            }
        }

        return new HandlerInfo(
            HandlerType: classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            RequestType: requestTypeSymbol.ToDisplayString(
                SymbolDisplayFormat.FullyQualifiedFormat
            ),
            ResponseType: responseTypeName,
            MarkerInterfaces: markerInterfaces,
            UnboundMarkerInterfaces: unboundMarkerInterfaces,
            HandlerInterface: handlerInterface.ToDisplayString(
                SymbolDisplayFormat.FullyQualifiedFormat
            ),
            SpecificBehaviors: specificBehaviors
        );
    }

    #endregion

    #region Source Generation Logic (GenerateMediatorClass)

    private static string GenerateMediatorClass(
        IReadOnlyList<HandlerInfo> handlers,
        IReadOnlyList<BehaviorInfo> availableBehaviors
    )
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine(
            @"// <auto-generated> by LinKit.Generator
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

        public Task SendAsync(ICommand command, CancellationToken cancellationToken = default)
        {
            return (object)command switch
            {"
        );

        IEnumerable<HandlerInfo> voidCommandHandlers = handlers.Where(h =>
            h.HandlerInterface.Contains(ICommandHandlerName) && h.ResponseType == UnitTypeName
        );
        foreach (HandlerInfo? handler in voidCommandHandlers)
        {
            sb.AppendLine(
                $"                {handler.RequestType} c => HandleVoidRequest(c, cancellationToken),"
            );
        }
        sb.AppendLine(
            @"                _ => throw new InvalidOperationException($""No handler found for command type {command.GetType().FullName}"")
            };
        }

        public Task<TResponse> SendAsync<TResponse>(ICommand<TResponse> command, CancellationToken cancellationToken = default)
        {
            return (object)command switch
            {"
        );

        IEnumerable<HandlerInfo> resultCommandHandlers = handlers.Where(h =>
            h.HandlerInterface.Contains(ICommandHandlerName) && h.ResponseType != UnitTypeName
        );
        foreach (HandlerInfo? handler in resultCommandHandlers)
        {
            sb.AppendLine(
                $"                {handler.RequestType} c => (Task<TResponse>)(object)HandleResultRequest(c, cancellationToken),"
            );
        }
        sb.AppendLine(
            @"                _ => throw new InvalidOperationException($""No handler found for command type {command.GetType().FullName}"")
            };
        }

        public Task<TResponse> QueryAsync<TResponse>(IQuery<TResponse> query, CancellationToken cancellationToken = default)
        {
            return (object)query switch
            {"
        );

        IEnumerable<HandlerInfo> queryHandlers = handlers.Where(h =>
            h.HandlerInterface.Contains(IQueryHandlerName)
        );
        foreach (HandlerInfo? handler in queryHandlers)
        {
            sb.AppendLine(
                $"                {handler.RequestType} q => (Task<TResponse>)(object)HandleResultRequest(q, cancellationToken),"
            );
        }
        sb.AppendLine(
            @"                _ => throw new InvalidOperationException($""No handler found for query type {query.GetType().FullName}"")
            };
        }
"
        );

        foreach (HandlerInfo? handler in handlers.Where(h => h.ResponseType != UnitTypeName))
        {
            sb.AppendLine(
                $@"
        private Task<{handler.ResponseType}> HandleResultRequest({handler.RequestType} request, CancellationToken cancellationToken)
        {{
            RequestHandlerDelegate<{handler.ResponseType}> next = () => _serviceProvider.GetRequiredService<{handler.HandlerInterface}>().HandleAsync(request, cancellationToken);
"
            );
            GeneratePipelineLogic(sb, handler, availableBehaviors, hasResult: true);
            sb.Append(
                @"
            return next();
        }
"
            );
        }

        foreach (HandlerInfo? handler in handlers.Where(h => h.ResponseType == UnitTypeName))
        {
            sb.AppendLine(
                $@"
        private async Task HandleVoidRequest({handler.RequestType} request, CancellationToken cancellationToken)
        {{
            Func<Task> next = () => _serviceProvider.GetRequiredService<{handler.HandlerInterface}>().HandleAsync(request, cancellationToken);
"
            );
            GeneratePipelineLogic(sb, handler, availableBehaviors, hasResult: false);
            sb.AppendLine("            await next();");
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }

    private static void GeneratePipelineLogic(
        StringBuilder sb,
        HandlerInfo handler,
        IReadOnlyList<BehaviorInfo> availableBehaviors,
        bool hasResult
    )
    {
        List<BehaviorInfo> applicableContractBehaviors = availableBehaviors
            .Where(b =>
            {
                bool targetMatch =
                    b.TargetInterface is null
                    || b.TargetInterface == "global::System.Object"
                    || handler.MarkerInterfaces.Contains(b.TargetInterface);

                if (!targetMatch)
                {
                    return false;
                }

                bool constraintsMatch = b.GenericConstraints.All(constraint =>
                    handler.UnboundMarkerInterfaces.Contains(constraint)
                    || handler.MarkerInterfaces.Contains(constraint)
                );

                return constraintsMatch;
            })
            .OrderBy(b => b.Order)
            .ToList();

        foreach (string? specificBehaviorType in handler.SpecificBehaviors.AsEnumerable().Reverse())
        {
            string closedBehaviorType =
                $"{specificBehaviorType.Split('<')[0]}<{handler.RequestType}, {handler.ResponseType}>";
            sb.AppendLine("            {");
            sb.AppendLine("                var capturedNext = next;");
            if (hasResult)
            {
                sb.AppendLine(
                    $@"                next = () => _serviceProvider.GetRequiredService<{closedBehaviorType}>().HandleAsync(request, capturedNext, cancellationToken);"
                );
            }
            else
            {
                sb.AppendLine(
                    $"                next = async () => {{ var _ = await _serviceProvider.GetRequiredService<{closedBehaviorType}>().HandleAsync(request, () => capturedNext().ContinueWith(_ => {UnitTypeName}.Value, cancellationToken), cancellationToken); }};"
                );
            }
            sb.AppendLine("            }");
        }

        foreach (BehaviorInfo? behavior in applicableContractBehaviors.AsEnumerable().Reverse())
        {
            string closedBehaviorType =
                $"{behavior.UnboundBehaviorType}<{handler.RequestType}, {handler.ResponseType}>";
            sb.AppendLine("            {");
            sb.AppendLine("                var capturedNext = next;");
            if (hasResult)
            {
                sb.AppendLine(
                    $@"                next = () => _serviceProvider.GetRequiredService<{closedBehaviorType}>().HandleAsync(request, capturedNext, cancellationToken);"
                );
            }
            else
            {
                sb.AppendLine(
                    $"                next = async () => {{ var _ = await _serviceProvider.GetRequiredService<{closedBehaviorType}>().HandleAsync(request, () => capturedNext().ContinueWith(_ => {UnitTypeName}.Value, cancellationToken), cancellationToken); }};"
                );
            }
            sb.AppendLine("            }");
        }
    }

    #endregion
}
