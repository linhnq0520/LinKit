using System;
using Microsoft.CodeAnalysis;

namespace LinKit.Generator.Generators;

internal static class UnsupportedPolicyAttributePart
{
    private static readonly DiagnosticDescriptor UnsupportedPolicyAttribute = new(
        id: "LKM003",
        title: "Policy attribute is not applied",
        messageFormat: "Attribute '{0}' is metadata only and has no generated behavior yet; use the corresponding explicit API",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly string[] AttributeMetadataNames =
    {
        "LinKit.Core.Caching.CacheAsideAttribute",
        "LinKit.Core.Caching.InvalidateCacheAttribute",
        "LinKit.Core.Resilience.RetryAttribute",
        "LinKit.Core.Resilience.CircuitBreakerAttribute",
        "LinKit.Core.Resilience.TimeoutAttribute"
    };

    public static void Initialize(IncrementalGeneratorInitializationContext context)
    {
        foreach (var metadataName in AttributeMetadataNames)
        {
            var attributeLocations = context.SyntaxProvider.ForAttributeWithMetadataName(
                metadataName,
                static (_, _) => true,
                static (attributeContext, _) =>
                {
                    var locations = new Location[attributeContext.Attributes.Length];
                    for (var index = 0; index < attributeContext.Attributes.Length; index++)
                    {
                        locations[index] = attributeContext.Attributes[index]
                            .ApplicationSyntaxReference?
                            .GetSyntax()
                            .GetLocation() ?? Location.None;
                    }

                    return locations;
                });

            var attributeName = metadataName.Substring(metadataName.LastIndexOf('.') + 1);
            context.RegisterSourceOutput(attributeLocations, (productionContext, locations) =>
            {
                foreach (var location in locations)
                {
                    productionContext.ReportDiagnostic(Diagnostic.Create(
                        UnsupportedPolicyAttribute,
                        location,
                        attributeName));
                }
            });
        }
    }
}
