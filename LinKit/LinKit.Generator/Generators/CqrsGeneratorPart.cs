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
        var collectedHandlers = GetCollectedHandlers(context);
        var collectedBehaviors = GetCollectedBehaviors(context);

        return collectedHandlers
            .Combine(collectedBehaviors)
            .Select(
                (source, _) =>
                {
                    var handlers = source.Left;
                    var behaviors = source.Right;
                    var services = new List<CqrsServiceInfo>();

                    if (!handlers.Any())
                    {
                        return (IReadOnlyList<CqrsServiceInfo>)services;
                    }

                    services.Add(
                        new CqrsServiceInfo(
                            "services.AddScoped<LinKit.Core.Cqrs.IMediator, LinKit.Generated.Cqrs.Mediator>();"
                        )
                    );

                    foreach (var handler in handlers)
                    {
                        services.Add(
                            new CqrsServiceInfo(
                                $"services.AddTransient<{handler.HandlerInterface}, {handler.HandlerType}>();"
                            )
                        );
                    }

                    if (behaviors.Any())
                    {
                        var registeredBehaviors = new HashSet<string>();

                        foreach (var handler in handlers)
                        {
                            var applicableContractBehaviors = behaviors.Where(b =>
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

                            var allApplicableBehaviors = handler
                                .SpecificBehaviors.Select(sb => sb.Split('<')[0])
                                .Concat(
                                    applicableContractBehaviors.Select(cb => cb.UnboundBehaviorType)
                                )
                                .Distinct();

                            foreach (var unboundBehaviorType in allApplicableBehaviors)
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
        var collectedHandlers = GetCollectedHandlers(context);
        var collectedBehaviors = GetCollectedBehaviors(context);

        var combined = collectedHandlers.Combine(collectedBehaviors);

        context.RegisterSourceOutput(
            combined,
            (spc, source) =>
            {
                var handlers = source.Left;
                var behaviors = source.Right;
                if (!handlers.Any())
                {
                    return;
                }

                var mediatorSource = GenerateMediatorClass(handlers, behaviors);
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
                    var contextSymbol = (INamedTypeSymbol)data.TargetSymbol;
                    var handlers = new List<INamedTypeSymbol>();
                    var attributeData = contextSymbol
                        .GetAttributes()
                        .FirstOrDefault(ad =>
                            ad.AttributeClass?.ToDisplayString() == ContextAttributeName
                        );
                    if (attributeData is null || attributeData.ConstructorArguments.Length == 0)
                    {
                        return ImmutableArray<INamedTypeSymbol>.Empty;
                    }

                    var constructorArgs = attributeData.ConstructorArguments[0];
                    if (constructorArgs.Kind != TypedConstantKind.Array)
                    {
                        return ImmutableArray<INamedTypeSymbol>.Empty;
                    }

                    foreach (var typeConstant in constructorArgs.Values)
                    {
                        if (typeConstant.Value is INamedTypeSymbol handlerTypeSymbol)
                        {
                            handlers.Add(handlerTypeSymbol);
                        }
                    }
                    return handlers.ToImmutableArray();
                }
            );

        var allHandlerSymbols = handlersFromAttribute
            .Collect()
            .Combine(handlersFromContext.Collect());

        return allHandlerSymbols.Select(
            (tuple, _) =>
            {
                var uniqueHandlers = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
                foreach (var handler in tuple.Left)
                {
                    uniqueHandlers.Add(handler);
                }

                foreach (var handler in tuple.Right)
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
                    var symbol = (INamedTypeSymbol)c.TargetSymbol;
                    var attributeData = symbol
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
                    var orderArg = attributeData.ConstructorArguments.FirstOrDefault(arg =>
                        arg.Type?.ToDisplayString() == "int"
                    );
                    var order = orderArg.IsNull ? 0 : (int)orderArg.Value!;

                    var constraints = new List<string>();
                    if (symbol.IsGenericType)
                    {
                        var typeParameter = symbol.TypeParameters.FirstOrDefault(tp =>
                            tp.Name == "TRequest"
                        );
                        if (typeParameter != null)
                        {
                            foreach (var constraintTypeSymbol in typeParameter.ConstraintTypes)
                            {
                                if (
                                    constraintTypeSymbol is INamedTypeSymbol namedConstraint
                                    && namedConstraint.IsGenericType
                                )
                                {
                                    var originalDefinition = namedConstraint.OriginalDefinition;
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

                    var originalSymbol = symbol.IsGenericType ? symbol.OriginalDefinition : symbol;
                    var unboundTypeName = originalSymbol
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
        var handlerInterface = classSymbol.AllInterfaces.FirstOrDefault(i =>
            i.OriginalDefinition.ToDisplayString().StartsWith(ICommandHandlerName)
            || i.OriginalDefinition.ToDisplayString().StartsWith(IQueryHandlerName)
        );

        if (handlerInterface is null || handlerInterface.TypeArguments.Length == 0)
        {
            return null;
        }

        var requestTypeSymbol = handlerInterface.TypeArguments[0];
        var responseTypeSymbol =
            handlerInterface.TypeArguments.Length > 1 ? handlerInterface.TypeArguments[1] : null;
        var responseTypeName =
            responseTypeSymbol?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            ?? UnitTypeName;
        var markerInterfaces = new List<string>();
        var unboundMarkerInterfaces = new List<string>();

        markerInterfaces.Add(
            requestTypeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
        );

        foreach (var implementedInterface in requestTypeSymbol.AllInterfaces)
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

        var specificBehaviors = new List<string>();
        var applyBehaviorAttributes = requestTypeSymbol
            .GetAttributes()
            .Where(ad => ad.AttributeClass?.ToDisplayString() == ApplyBehaviorAttributeName);
        foreach (var attr in applyBehaviorAttributes)
        {
            if (
                attr.ConstructorArguments.Length > 0
                && attr.ConstructorArguments[0].Kind == TypedConstantKind.Array
            )
            {
                foreach (var typeConstant in attr.ConstructorArguments[0].Values)
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
        var sb = new StringBuilder();
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

        var voidCommandHandlers = handlers.Where(h =>
            h.HandlerInterface.Contains(ICommandHandlerName) && h.ResponseType == UnitTypeName
        );
        foreach (var handler in voidCommandHandlers)
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

        var resultCommandHandlers = handlers.Where(h =>
            h.HandlerInterface.Contains(ICommandHandlerName) && h.ResponseType != UnitTypeName
        );
        foreach (var handler in resultCommandHandlers)
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

        var queryHandlers = handlers.Where(h => h.HandlerInterface.Contains(IQueryHandlerName));
        foreach (var handler in queryHandlers)
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

        foreach (var handler in handlers.Where(h => h.ResponseType != UnitTypeName))
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

        foreach (var handler in handlers.Where(h => h.ResponseType == UnitTypeName))
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
        var applicableContractBehaviors = availableBehaviors
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

        foreach (var specificBehaviorType in handler.SpecificBehaviors.AsEnumerable().Reverse())
        {
            var closedBehaviorType =
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

        foreach (var behavior in applicableContractBehaviors.AsEnumerable().Reverse())
        {
            var closedBehaviorType =
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
