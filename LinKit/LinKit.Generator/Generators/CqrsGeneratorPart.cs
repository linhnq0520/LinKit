using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace LinKit.Generator.Generators;

internal static class CqrsGeneratorPart
{
    private const string ICommandHandlerName = "LinKit.Core.Cqrs.ICommandHandler";
    private const string IQueryHandlerName = "LinKit.Core.Cqrs.IQueryHandler";
    private const string HandlerAttributeName = "LinKit.Core.Cqrs.CqrsHandlerAttribute";
    private const string ContextAttributeName = "LinKit.Core.Cqrs.CqrsContextAttribute";
    private const string BehaviorAttributeName = "LinKit.Core.Cqrs.CqrsBehaviorAttribute";
    private const string ApplyBehaviorAttributeName = "LinKit.Core.Cqrs.ApplyBehaviorAttribute";

    public static IncrementalValueProvider<IReadOnlyList<CqrsServiceInfo>> GetServices(
        IncrementalGeneratorInitializationContext context
    )
    {
        // Pipeline 1 & 2: Get Handlers info
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
                        return ImmutableArray<INamedTypeSymbol>.Empty;
                    var constructorArgs = attributeData.ConstructorArguments[0];
                    if (constructorArgs.Kind != TypedConstantKind.Array)
                        return ImmutableArray<INamedTypeSymbol>.Empty;
                    foreach (var typeConstant in constructorArgs.Values)
                    {
                        if (typeConstant.Value is INamedTypeSymbol handlerTypeSymbol)
                            handlers.Add(handlerTypeSymbol);
                    }
                    return handlers.ToImmutableArray();
                }
            );

        var allHandlerSymbols = handlersFromAttribute
            .Collect()
            .Combine(handlersFromContext.Collect());
        IncrementalValueProvider<IReadOnlyList<HandlerInfo>> collectedHandlers =
            allHandlerSymbols.Select(
                (tuple, _) =>
                {
                    var uniqueHandlers = new HashSet<INamedTypeSymbol>(
                        SymbolEqualityComparer.Default
                    );
                    foreach (var handler in tuple.Left)
                        uniqueHandlers.Add(handler);
                    foreach (var handler in tuple.Right)
                        uniqueHandlers.Add(handler);
                    return (IReadOnlyList<HandlerInfo>)
                        uniqueHandlers
                            .Select(GetHandlerInfo)
                            .Where(info => info is not null)
                            .ToList();
                }
            );

        // Pipeline 3: Get Behaviors info
        IncrementalValuesProvider<BehaviorInfo?> allBehaviors = context
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
                        return null;
                    var targetInterfaceType =
                        attributeData.ConstructorArguments[0].Value as INamedTypeSymbol;
                    if (targetInterfaceType is null)
                        return null;
                    var order =
                        attributeData.ConstructorArguments.Length > 1
                            ? (int)attributeData.ConstructorArguments[1].Value!
                            : 0;
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
                        )
                    );
                }
            )
            .Where(info => info is not null);

        var collectedBehaviors = allBehaviors.Collect();

        // Return service info instead of generating files
        return collectedHandlers
            .Combine(collectedBehaviors)
            .Select(
                (source, _) =>
                {
                    var handlers = source.Left;
                    var behaviors = source.Right;

                    var services = new List<CqrsServiceInfo>();

                    if (handlers.Any())
                    {
                        services.Add(
                            new CqrsServiceInfo("services.AddSingleton<LinKit.Core.Cqrs.IMediator, LinKit.Core.Cqrs.Mediator>();")
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
                            foreach (var behavior in behaviors)
                            {
                                if (registeredBehaviors.Add(behavior.UnboundBehaviorType))
                                {
                                    services.Add(
                                        new CqrsServiceInfo(
                                            $"services.AddTransient(typeof({behavior.UnboundBehaviorType}<,>));"
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

    public static void GenerateNonDIFiles(IncrementalGeneratorInitializationContext context)
    {
        // Pipeline 1 & 2: Get Handlers info (duplicated from GetServices - could be optimized)
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
                        return ImmutableArray<INamedTypeSymbol>.Empty;
                    var constructorArgs = attributeData.ConstructorArguments[0];
                    if (constructorArgs.Kind != TypedConstantKind.Array)
                        return ImmutableArray<INamedTypeSymbol>.Empty;
                    foreach (var typeConstant in constructorArgs.Values)
                    {
                        if (typeConstant.Value is INamedTypeSymbol handlerTypeSymbol)
                            handlers.Add(handlerTypeSymbol);
                    }
                    return handlers.ToImmutableArray();
                }
            );

        var allHandlerSymbols = handlersFromAttribute
            .Collect()
            .Combine(handlersFromContext.Collect());
        IncrementalValueProvider<IReadOnlyList<HandlerInfo>> collectedHandlers =
            allHandlerSymbols.Select(
                (tuple, _) =>
                {
                    var uniqueHandlers = new HashSet<INamedTypeSymbol>(
                        SymbolEqualityComparer.Default
                    );
                    foreach (var handler in tuple.Left)
                        uniqueHandlers.Add(handler);
                    foreach (var handler in tuple.Right)
                        uniqueHandlers.Add(handler);
                    return (IReadOnlyList<HandlerInfo>)
                        uniqueHandlers
                            .Select(GetHandlerInfo)
                            .Where(info => info is not null)
                            .ToList();
                }
            );

        // Pipeline 3: Get Behaviors info
        IncrementalValuesProvider<BehaviorInfo?> allBehaviors = context
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
                        return null;
                    var targetInterfaceType =
                        attributeData.ConstructorArguments[0].Value as INamedTypeSymbol;
                    if (targetInterfaceType is null)
                        return null;
                    var order =
                        attributeData.ConstructorArguments.Length > 1
                            ? (int)attributeData.ConstructorArguments[1].Value!
                            : 0;
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
                        )
                    );
                }
            )
            .Where(info => info is not null);

        var collectedBehaviors = allBehaviors.Collect();

        var combined = collectedHandlers.Combine(collectedBehaviors);

        context.RegisterSourceOutput(
            combined,
            (spc, source) =>
            {
                var handlers = source.Left;
                var behaviors = source.Right;
                if (!handlers.Any())
                    return;

                var extensionsSource = SourceGenerationHelper.GenerateMediatorExtensions(
                    handlers,
                    behaviors
                );
                spc.AddSource(
                    "Cqrs.Mediator.Extensions.g.cs",
                    SourceText.From(extensionsSource, Encoding.UTF8)
                );
            }
        );
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
            ?? "System.ValueTuple";

        var markerInterfaces = requestTypeSymbol
            .AllInterfaces.Select(i => i.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
            .ToList();

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
                        specificBehaviors.Add(
                            behaviorTypeSymbol.ToDisplayString(
                                SymbolDisplayFormat.FullyQualifiedFormat
                            )
                        );
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
            HandlerInterface: handlerInterface.ToDisplayString(
                SymbolDisplayFormat.FullyQualifiedFormat
            )
        );
    }
}

internal record HandlerInfo(
    string HandlerType,
    string RequestType,
    string ResponseType,
    IReadOnlyList<string> MarkerInterfaces,
    string HandlerInterface
);

internal record BehaviorInfo(string UnboundBehaviorType, int Order, string TargetInterface);
