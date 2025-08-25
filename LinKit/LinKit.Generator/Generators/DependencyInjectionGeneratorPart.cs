using System.Collections.Generic;
using System.Linq;
using System.Text;
using LinKit.Core.Abstractions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace LinKit.Generator.Generators;

internal static class DependencyInjectionGeneratorPart
{
    private const string RegisterServiceAttributeName =
        "LinKit.Core.Abstractions.RegisterServiceAttribute";

    // Hàm mới: chỉ trả về danh sách ServiceInfo
    public static IncrementalValueProvider<IReadOnlyList<ServiceInfo>> GetServices(
        IncrementalGeneratorInitializationContext context
    )
    {
        IncrementalValuesProvider<(
            INamedTypeSymbol Implementation,
            AttributeData Attribute
        )> serviceDeclarations = context
            .SyntaxProvider.ForAttributeWithMetadataName(
                RegisterServiceAttributeName,
                predicate: (node, _) => node is ClassDeclarationSyntax,
                transform: (ctx, _) =>
                    (
                        Implementation: (INamedTypeSymbol)ctx.TargetSymbol,
                        Attribute: ctx.Attributes[0]
                    )
            )
            .Where(x => x.Implementation is not null);

        return serviceDeclarations
            .Collect()
            .Select(
                (services, _) =>
                {
                    var serviceInfos = new List<ServiceInfo>();
                    foreach (var (implementation, attribute) in services)
                    {
                        var lifetime = (int)(attribute.ConstructorArguments[0].Value ?? 0);
                        var serviceTypeSymbol =
                            attribute.ConstructorArguments[1].Value as INamedTypeSymbol;
                        var serviceTypeName =
                            serviceTypeSymbol?.ToDisplayString(
                                SymbolDisplayFormat.FullyQualifiedFormat
                            )
                            ?? implementation
                                .AllInterfaces.FirstOrDefault()
                                ?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                            ?? implementation.ToDisplayString(
                                SymbolDisplayFormat.FullyQualifiedFormat
                            );

                        serviceInfos.Add(
                            new ServiceInfo(
                                serviceTypeName,
                                implementation.ToDisplayString(
                                    SymbolDisplayFormat.FullyQualifiedFormat
                                ),
                                lifetime
                            )
                        );
                    }
                    return (IReadOnlyList<ServiceInfo>)serviceInfos;
                }
            );
    }

    // Hàm này không còn cần thiết sinh file DI riêng nữa, có thể bỏ hoặc giữ lại cho mục đích khác
    public static void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Không cần sinh file DI riêng nữa
    }
}

public record ServiceInfo(string ServiceType, string ImplementationType, int Lifetime);
