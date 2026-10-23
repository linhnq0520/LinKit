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
    private const string AddTransientAttributeName = "LinKit.Core.Abstractions.AddTransientAttribute";
    private const string AddScopedAttributeName = "LinKit.Core.Abstractions.AddScopedAttribute";
    private const string AddSingletonAttributeName = "LinKit.Core.Abstractions.AddSingletonAttribute";

    public static IncrementalValueProvider<IReadOnlyList<ServiceInfo>> GetServices(
        IncrementalGeneratorInitializationContext context
    )
    {
        // FixedLifetime < 0 means the attribute carries its own Lifetime argument ([RegisterService]).
        IncrementalValuesProvider<(
            INamedTypeSymbol Implementation,
            AttributeData Attribute,
            int FixedLifetime
        )> Declare(string attributeName, int fixedLifetime) =>
            context
                .SyntaxProvider.ForAttributeWithMetadataName(
                    attributeName,
                    predicate: (node, _) => node is ClassDeclarationSyntax,
                    transform: (ctx, _) =>
                        ctx.Attributes.Select(attr =>
                            (
                                Implementation: (INamedTypeSymbol)ctx.TargetSymbol,
                                Attribute: attr,
                                FixedLifetime: fixedLifetime
                            )
                        )
                )
                .SelectMany((items, _) => items)
                .Where(x => x.Implementation is not null);

        var serviceDeclarations = Declare(RegisterServiceAttributeName, -1)
            .Collect()
            .Combine(Declare(AddTransientAttributeName, 0).Collect())
            .Combine(Declare(AddScopedAttributeName, 1).Collect())
            .Combine(Declare(AddSingletonAttributeName, 2).Collect())
            .Select(
                (all, _) =>
                    all.Left.Left.Left.Concat(all.Left.Left.Right)
                        .Concat(all.Left.Right)
                        .Concat(all.Right)
                        .ToList()
            );

        return serviceDeclarations
            .Select(
                (services, _) =>
                {
                    var serviceInfos = new List<ServiceInfo>();
                    foreach (var (implementation, attribute, fixedLifetime) in services)
                    {
                        bool shorthand = fixedLifetime >= 0;
                        var lifetime = shorthand
                            ? fixedLifetime
                            : GetParameter<int>(attribute, 0, "Lifetime", 0);
                        var serviceTypeSymbol = GetParameter<INamedTypeSymbol>(
                            attribute,
                            shorthand ? 0 : 1,
                            "ServiceType",
                            null
                        );
                        var key = GetParameter<string>(attribute, shorthand ? -1 : 2, "Key", null);
                        var isGeneric = GetParameter<bool>(
                            attribute,
                            shorthand ? -1 : 3,
                            "IsGeneric",
                            false
                        );

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
            constructorArgIndex >= 0
            && constructorArgIndex < attribute.ConstructorArguments.Length
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
