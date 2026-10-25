using Microsoft.CodeAnalysis;

namespace LinKit.Generator.Generators;

internal static class GeneratedNamespaceProvider
{
    public static IncrementalValueProvider<string> Create(IncrementalGeneratorInitializationContext context)
    {
        var rootNamespace = context.AnalyzerConfigOptionsProvider.Select(
            static (provider, _) =>
                provider.GlobalOptions.TryGetValue("build_property.RootNamespace", out var value)
                    ? value
                    : string.Empty);
        var assemblyName = context.CompilationProvider.Select(
            static (compilation, _) => compilation.AssemblyName ?? string.Empty);

        return rootNamespace.Combine(assemblyName).Select(
            static (namespaces, _) =>
            {
                var rootNamespaceValue = string.IsNullOrWhiteSpace(namespaces.Left)
                    ? namespaces.Right
                    : namespaces.Left;
                return string.IsNullOrWhiteSpace(rootNamespaceValue)
                    ? "Generated"
                    : $"{rootNamespaceValue}.Generated";
            });
    }
}
