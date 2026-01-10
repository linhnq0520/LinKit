using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace LinKit.Generator.Generators;

public record ServiceInfo(
    string ServiceType,
    string ImplementationType,
    int Lifetime,
    string? Key = null,
    bool IsGeneric = false
);

internal static class DependencyInjectionGeneratorPart
{
    private const string RegisterServiceAttributeName =
        "LinKit.Core.Abstractions.RegisterServiceAttribute";

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
                    ctx.Attributes.Select(attr =>
                        (Implementation: (INamedTypeSymbol)ctx.TargetSymbol, Attribute: attr)
                    )
            )
            .SelectMany((items, _) => items)
            .Where(x => x.Implementation is not null);

        return serviceDeclarations
            .Collect()
            .Select(
                (services, _) =>
                {
                    var serviceInfos = new List<ServiceInfo>();
                    foreach (var (implementation, attribute) in services)
                    {
                        var lifetime = GetParameter<int>(attribute, 0, "Lifetime", 0);
                        var serviceTypeSymbol = GetParameter<INamedTypeSymbol>(
                            attribute,
                            1,
                            "ServiceType",
                            null
                        );
                        var key = GetParameter<string>(attribute, 2, "Key", null);
                        var isGeneric = GetParameter<bool>(attribute, 3, "IsGeneric", false);

                        if (serviceTypeSymbol == null)
                        {
                            var allInterfaces = implementation.AllInterfaces;
                            var leafInterfaces = allInterfaces
                                .Where(i =>
                                    !allInterfaces.Any(other =>
                                        other.AllInterfaces.Contains(
                                            i,
                                            SymbolEqualityComparer.Default
                                        )
                                    )
                                )
                                .ToList();

                            serviceTypeSymbol =
                                leafInterfaces.FirstOrDefault(i =>
                                    i.Name == $"I{implementation.Name}"
                                )
                                ?? leafInterfaces.FirstOrDefault()
                                ?? implementation;
                        }

                        string serviceTypeName = FormatTypeName(serviceTypeSymbol, isGeneric);
                        string implementationTypeName = FormatTypeName(implementation, isGeneric);

                        serviceInfos.Add(
                            new ServiceInfo(
                                serviceTypeName,
                                implementationTypeName,
                                lifetime,
                                key,
                                isGeneric
                            )
                        );
                    }
                    return (IReadOnlyList<ServiceInfo>)serviceInfos;
                }
            );
    }

    private static string FormatTypeName(ITypeSymbol type, bool isGeneric)
    {
        string name = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        if (isGeneric && type is INamedTypeSymbol namedType)
        {
            var openGenericType = namedType.ConstructedFrom ?? namedType;
            name = openGenericType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            int paramCount = openGenericType.TypeParameters.Length;
            if (paramCount > 0)
            {
                string genericPlaceholder = "<" + new string(',', paramCount - 1) + ">";
                name = Regex.Replace(name, @"<[^>]+>", genericPlaceholder);
            }
        }
        return name;
    }

    private static T? GetParameter<T>(
        AttributeData attribute,
        int constructorArgIndex,
        string namedArgKey,
        T? defaultValue
    )
    {
        if (
            constructorArgIndex < attribute.ConstructorArguments.Length
            && !attribute.ConstructorArguments[constructorArgIndex].IsNull
        )
        {
            var value = attribute.ConstructorArguments[constructorArgIndex].Value;
            if (value is T typedValue)
                return typedValue;
        }

        var namedArg = attribute.NamedArguments.FirstOrDefault(a => a.Key == namedArgKey);
        if (namedArg.Key != null && namedArg.Value.Value is T typedNamedValue)
            return typedNamedValue;

        return defaultValue;
    }
}
