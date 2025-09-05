using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;

namespace LinKit.Generator.Generators;

internal record HandlerInfo(
    string HandlerType,
    string RequestType,
    string ResponseType,
    IReadOnlyList<string> MarkerInterfaces,
    string HandlerInterface,
    IReadOnlyList<string> SpecificBehaviors
);

internal record BehaviorInfo(string UnboundBehaviorType, int Order, string TargetInterface);

internal static class CqrsGeneratorPart
{
    private const string ICommandHandlerName = "LinKit.Core.Cqrs.ICommandHandler";
    private const string IQueryHandlerName = "LinKit.Core.Cqrs.IQueryHandler";
    private const string HandlerAttributeName = "LinKit.Core.Cqrs.CqrsHandlerAttribute";
    private const string ContextAttributeName = "LinKit.Core.Cqrs.CqrsContextAttribute";
    private const string BehaviorAttributeName = "LinKit.Core.Cqrs.CqrsBehaviorAttribute";
    private const string ApplyBehaviorAttributeName = "LinKit.Core.Cqrs.ApplyBehaviorAttribute";

    #region Pipeline Setup (Initialize and GetServices)

    public static IncrementalValueProvider<IReadOnlyList<CqrsServiceInfo>> GetServices(
        IncrementalGeneratorInitializationContext context)
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

                    if (handlers.Any())
                    {
                        services.Add(new CqrsServiceInfo("services.AddSingleton<LinKit.Core.Cqrs.IMediator, LinKit.Generated.Cqrs.Mediator>();"));

                        foreach (var handler in handlers)
                        {
                            services.Add(new CqrsServiceInfo($"services.AddTransient<{handler.HandlerInterface}, {handler.HandlerType}>();"));
                        }

                        if (behaviors.Any())
                        {
                            var registeredBehaviors = new HashSet<string>();
                            foreach (var behavior in behaviors)
                            {
                                if (behavior is not null && registeredBehaviors.Add(behavior.UnboundBehaviorType))
                                {
                                    services.Add(new CqrsServiceInfo($"services.AddTransient(typeof({behavior.UnboundBehaviorType}<,>));"));
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

    private static IncrementalValueProvider<IReadOnlyList<HandlerInfo>> GetCollectedHandlers(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<INamedTypeSymbol> handlersFromAttribute = context.SyntaxProvider
            .ForAttributeWithMetadataName(HandlerAttributeName, (n, _) => n is ClassDeclarationSyntax, (c, _) => (INamedTypeSymbol)c.TargetSymbol)
            .Where(s => s is not null);

        IncrementalValuesProvider<INamedTypeSymbol> handlersFromContext = context.SyntaxProvider
            .ForAttributeWithMetadataName(ContextAttributeName, (n, _) => n is ClassDeclarationSyntax, (c, _) => c)
            .SelectMany((data, _) =>
            {
                var contextSymbol = (INamedTypeSymbol)data.TargetSymbol;
                var handlers = new List<INamedTypeSymbol>();
                var attributeData = contextSymbol.GetAttributes().FirstOrDefault(ad => ad.AttributeClass?.ToDisplayString() == ContextAttributeName);
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
            });

        var allHandlerSymbols = handlersFromAttribute.Collect().Combine(handlersFromContext.Collect());

        return allHandlerSymbols.Select((tuple, _) =>
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

            return (IReadOnlyList<HandlerInfo>)uniqueHandlers
                .Select(GetHandlerInfo)
                .Where(info => info is not null)!
                .ToList();
        });
    }

    private static IncrementalValueProvider<ImmutableArray<BehaviorInfo?>> GetCollectedBehaviors(IncrementalGeneratorInitializationContext context)
    {
        return context.SyntaxProvider
            .ForAttributeWithMetadataName(
                fullyQualifiedMetadataName: BehaviorAttributeName,
                predicate: (n, _) => n is ClassDeclarationSyntax,
                transform: (c, _) =>
                {
                    var symbol = (INamedTypeSymbol)c.TargetSymbol;
                    var attributeData = symbol.GetAttributes().First(ad => ad.AttributeClass?.ToDisplayString() == BehaviorAttributeName);
                    if (attributeData.ConstructorArguments.Length == 0)
                    {
                        return null;
                    }

                    if (attributeData.ConstructorArguments.FirstOrDefault(arg => arg.Type?.ToDisplayString() == "System.Type").Value is not INamedTypeSymbol targetInterfaceType)
                    {
                        return null;
                    }
                    var orderArg = attributeData.ConstructorArguments.FirstOrDefault(arg => arg.Type?.ToDisplayString() == "int");
                    var order = orderArg.IsNull ? 0 : (int)orderArg.Value!;

                    var originalSymbol = symbol.IsGenericType ? symbol.OriginalDefinition : symbol;
                    var unboundTypeName = originalSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat.WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Included)).Split('<')[0];

                    return new BehaviorInfo(unboundTypeName, order, targetInterfaceType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                })
            .Where(info => info is not null)!
            .Collect();
    }

    private static HandlerInfo? GetHandlerInfo(INamedTypeSymbol classSymbol)
    {
        var handlerInterface = classSymbol.AllInterfaces.FirstOrDefault(i =>
            i.OriginalDefinition.ToDisplayString().StartsWith(ICommandHandlerName) ||
            i.OriginalDefinition.ToDisplayString().StartsWith(IQueryHandlerName));

        if (handlerInterface is null || handlerInterface.TypeArguments.Length == 0)
        {
            return null;
        }

        var requestTypeSymbol = handlerInterface.TypeArguments[0];
        var responseTypeSymbol = handlerInterface.TypeArguments.Length > 1 ? handlerInterface.TypeArguments[1] : null;
        var responseTypeName = responseTypeSymbol?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? "System.ValueTuple";
        var markerInterfaces = requestTypeSymbol.AllInterfaces.Select(i => i.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)).ToList();

        var specificBehaviors = new List<string>();
        var applyBehaviorAttributes = requestTypeSymbol.GetAttributes().Where(ad => ad.AttributeClass?.ToDisplayString() == ApplyBehaviorAttributeName);
        foreach (var attr in applyBehaviorAttributes)
        {
            if (attr.ConstructorArguments.Length > 0 && attr.ConstructorArguments[0].Kind == TypedConstantKind.Array)
            {
                foreach (var typeConstant in attr.ConstructorArguments[0].Values)
                {
                    if (typeConstant.Value is INamedTypeSymbol behaviorTypeSymbol)
                    {
                        specificBehaviors.Add(behaviorTypeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                    }
                }
            }
        }

        return new HandlerInfo(
            HandlerType: classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            RequestType: requestTypeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            ResponseType: responseTypeName,
            MarkerInterfaces: markerInterfaces,
            HandlerInterface: handlerInterface.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            SpecificBehaviors: specificBehaviors
        );
    }

    #endregion

    #region Source Generation Logic (GenerateMediatorClass)

    private static string GenerateMediatorClass(IReadOnlyList<HandlerInfo> handlers, IReadOnlyList<BehaviorInfo> availableBehaviors)
    {
        var sb = new StringBuilder();
        sb.AppendLine(@"// <auto-generated> by LinKit.Generator
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

        public Task SendAsync<TCommand>(TCommand command, CancellationToken cancellationToken = default) where TCommand : ICommand
        {
            return (object)command switch
            {");

        foreach (var handler in handlers.Where(h => h.ResponseType == "System.ValueTuple"))
        {
            sb.AppendLine($"                {handler.RequestType} c => HandleVoidRequest(c, cancellationToken),");
        }
        sb.AppendLine(@"                _ => throw new InvalidOperationException($""No handler found for command type {command.GetType().FullName}"")
            };
        }

        public Task<TResult> SendAsync<TCommand, TResult>(TCommand command, CancellationToken cancellationToken = default) where TCommand : ICommand<TResult>
        {
            return (object)command switch
            {");
        foreach (var handler in handlers.Where(h => h.HandlerInterface.Contains(ICommandHandlerName) && h.ResponseType != "System.ValueTuple"))
        {
            sb.AppendLine($"                {handler.RequestType} c => (Task<TResult>)(object)HandleResultRequest(c, cancellationToken),");
        }
        sb.AppendLine(@"                _ => throw new InvalidOperationException($""No handler found for command type {command.GetType().FullName}"")
            };
        }

        public Task<TResult> QueryAsync<TQuery, TResult>(TQuery query, CancellationToken cancellationToken = default) where TQuery : IQuery<TResult>
        {
            return (object)query switch
            {");
        foreach (var handler in handlers.Where(h => h.HandlerInterface.Contains(IQueryHandlerName)))
        {
            sb.AppendLine($"                {handler.RequestType} q => (Task<TResult>)(object)HandleResultRequest(q, cancellationToken),");
        }
        sb.AppendLine(@"                _ => throw new InvalidOperationException($""No handler found for query type {query.GetType().FullName}"")
            };
        }
");

        foreach (var handler in handlers.Where(h => h.ResponseType != "System.ValueTuple"))
        {
            sb.AppendLine($@"
        private Task<{handler.ResponseType}> HandleResultRequest({handler.RequestType} request, CancellationToken cancellationToken)
        {{
            RequestHandlerDelegate<{handler.ResponseType}> next = () => 
                _serviceProvider.GetRequiredService<{handler.HandlerInterface}>()
                                .HandleAsync(request, cancellationToken);
");
            #region Pipeline Logic
            var applicableContractBehaviors = availableBehaviors.Where(b => b.TargetInterface is null || handler.MarkerInterfaces.Contains(b.TargetInterface)).OrderBy(b => b.Order).ToList();
            foreach (var specificBehaviorType in handler.SpecificBehaviors.AsEnumerable().Reverse())
            {
                sb.AppendLine("            { var capturedNext = next;");
                var closedBehaviorType = $"{specificBehaviorType.Split('<')[0]}<{handler.RequestType}, {handler.ResponseType}>";
                sb.AppendLine($@"                next = () => _serviceProvider.GetRequiredService<{closedBehaviorType}>().HandleAsync(request, capturedNext, cancellationToken); }}");
            }
            foreach (var behavior in applicableContractBehaviors.AsEnumerable().Reverse())
            {
                sb.AppendLine("            { var capturedNext = next;");
                var closedBehaviorType = $"{behavior.UnboundBehaviorType}<{handler.RequestType}, {handler.ResponseType}>";
                sb.AppendLine($@"                next = () => _serviceProvider.GetRequiredService<{closedBehaviorType}>().HandleAsync(request, capturedNext, cancellationToken); }}");
            }
            #endregion

            sb.Append(@"
            return next();
        }
");
        }

        foreach (var handler in handlers.Where(h => h.ResponseType == "System.ValueTuple"))
        {
            sb.AppendLine($@"
        private async Task HandleVoidRequest({handler.RequestType} request, CancellationToken cancellationToken)
        {{
            Func<Task> next = () => 
                _serviceProvider.GetRequiredService<{handler.HandlerInterface}>()
                                .HandleAsync(request, cancellationToken);
");
            #region Pipeline Logic
            var applicableContractBehaviors = availableBehaviors.Where(b => b.TargetInterface is null || handler.MarkerInterfaces.Contains(b.TargetInterface)).OrderBy(b => b.Order).ToList();
            foreach (var specificBehaviorType in handler.SpecificBehaviors.AsEnumerable().Reverse())
            {
                sb.AppendLine("            { var capturedNext = next;");
                var closedBehaviorType = $"{specificBehaviorType.Split('<')[0]}<{handler.RequestType}, {handler.ResponseType}>";
                sb.AppendLine($@"                next = async () => {{ await _serviceProvider.GetRequiredService<{closedBehaviorType}>().HandleAsync(request, async () => {{ await capturedNext(); return default; }}, cancellationToken); }}; }}");
            }
            foreach (var behavior in applicableContractBehaviors.AsEnumerable().Reverse())
            {
                sb.AppendLine("            { var capturedNext = next;");
                var closedBehaviorType = $"{behavior.UnboundBehaviorType}<{handler.RequestType}, {handler.ResponseType}>";
                sb.AppendLine($@"                next = async () => {{ await _serviceProvider.GetRequiredService<{closedBehaviorType}>().HandleAsync(request, async () => {{ await capturedNext(); return default; }}, cancellationToken); }}; }}");
            }
            #endregion

            sb.Append(@"
            await next();
        }
");
        }

        sb.AppendLine(@"    }
}");
        return sb.ToString();
    }

    #endregion
}