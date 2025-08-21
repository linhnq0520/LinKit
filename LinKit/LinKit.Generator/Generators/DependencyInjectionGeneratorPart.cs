using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using static LinKit.Generator.Generators.SourceGenerationHelper;

namespace LinKit.Generator.Generators;

internal static class DependencyInjectionGeneratorPart
{
    private const string RegisterServiceAttributeName = "LinKit.Core.Abstractions.RegisterServiceAttribute";

    public static void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<(INamedTypeSymbol Implementation, AttributeData Attribute)> serviceDeclarations = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                RegisterServiceAttributeName,
                predicate: (node, _) => node is ClassDeclarationSyntax,
                transform: (ctx, _) => (Implementation: (INamedTypeSymbol)ctx.TargetSymbol, Attribute: ctx.Attributes[0]))
            .Where(x => x.Implementation is not null);

        IncrementalValueProvider<IReadOnlyList<ServiceInfo>> collectedServices =
            serviceDeclarations.Collect().Select((services, _) =>
            {
                var serviceInfos = new List<ServiceInfo>();
                foreach (var (implementation, attribute) in services)
                {
                    var lifetime = (int)(attribute.ConstructorArguments[0].Value ?? 0);
                    var serviceTypeSymbol = attribute.ConstructorArguments[1].Value as INamedTypeSymbol;
                    var serviceTypeName = serviceTypeSymbol?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                                          ?? implementation.AllInterfaces.FirstOrDefault()?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                                          ?? implementation.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

                    serviceInfos.Add(new ServiceInfo(
                        serviceTypeName,
                        implementation.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                        lifetime));
                }
                return (IReadOnlyList<ServiceInfo>)serviceInfos;
            });

        context.RegisterSourceOutput(collectedServices, (spc, services) =>
        {
            if (!services.Any()) return;
            spc.AddSource("Services.DependencyInjection.g.cs", SourceText.From(GenerateServicesDI(services), Encoding.UTF8));
        });
    }
}