using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Text;

namespace LinKit.Generator.Generators;

internal sealed record ForMemberRule(
    string DestinationMember,
    string? SourceExpression,
    bool Ignore = false
);

internal sealed record MapConfig(
    INamedTypeSymbol SourceSymbol,
    INamedTypeSymbol DestSymbol,
    List<ForMemberRule> Rules
);

internal sealed record MapConfigWithDiags(MapConfig Config, ImmutableArray<Diagnostic> Diagnostics);

internal sealed record MapperInfo(
    string Namespace,
    string SourceType,
    string DestType,
    string DestShortName,
    IReadOnlyList<(string DestProp, string SourceExpr)> Assignments
);

public static class MapperGeneratorPart
{
    private const string MapperContextAttr = "LinKit.Core.Mapping.MapperContextAttribute";

    private static readonly DiagnosticDescriptor InvalidMappingConfiguration = new(
        id: "LKM002",
        title: "Invalid mapping configuration",
        messageFormat: "Invalid mapping configuration: {0}",
        category: "Mapping",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public static void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var mapperContexts = context.SyntaxProvider.ForAttributeWithMetadataName(
            MapperContextAttr,
            static (node, _) =>
                node is ClassDeclarationSyntax c
                && c.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)),
            static (ctx, _) => (ClassDeclarationSyntax)ctx.TargetNode
        );

        var mapConfigsPerClass = mapperContexts
            .Combine(context.CompilationProvider)
            .Select(
                static (tuple, _) =>
                {
                    var (classSyntax, compilation) = tuple;
                    var model = compilation.GetSemanticModel(classSyntax.SyntaxTree);
                    if (model.GetDeclaredSymbol(classSyntax) is not INamedTypeSymbol classSymbol)
                    {
                        return (Namespace: "", Configs: ImmutableArray<MapConfigWithDiags>.Empty);
                    }
                    string classNamespace = classSymbol.ContainingNamespace.IsGlobalNamespace
                        ? ""
                        : classSymbol.ContainingNamespace.ToDisplayString();

                    var configureMethodSyntax = classSyntax
                        .Members.OfType<MethodDeclarationSyntax>()
                        .FirstOrDefault(m =>
                            m.Identifier.Text == "Configure"
                            && m.ParameterList.Parameters.Count == 1
                        );

                    if (configureMethodSyntax is null)
                    {
                        return (
                            Namespace: classNamespace,
                            Configs: ImmutableArray<MapConfigWithDiags>.Empty
                        );
                    }

                    var configsWithDiags = new List<MapConfigWithDiags>();
                    foreach (
                        var inv in configureMethodSyntax
                            .DescendantNodes()
                            .OfType<InvocationExpressionSyntax>()
                    )
                    {
                        if (
                            inv.Expression is MemberAccessExpressionSyntax ma
                            && ma.Name is GenericNameSyntax gns
                            && gns.Identifier.Text == "CreateMap"
                        )
                        {
                            var typeArgs = gns.TypeArgumentList.Arguments;
                            if (typeArgs.Count != 2)
                            {
                                continue;
                            }

                            if (
                                model.GetTypeInfo(typeArgs[0]).Type is not INamedTypeSymbol srcType
                                || model.GetTypeInfo(typeArgs[1]).Type
                                    is not INamedTypeSymbol dstType
                            )
                            {
                                continue;
                            }

                            var (rules, diags) = CollectForMemberChain(inv, model);
                            var cfg = new MapConfig(srcType, dstType, rules);
                            configsWithDiags.Add(
                                new MapConfigWithDiags(cfg, diags.ToImmutableArray())
                            );
                        }
                    }
                    return (
                        Namespace: classNamespace,
                        Configs: configsWithDiags.ToImmutableArray()
                    );
                }
            );

        var allMapConfigs = mapConfigsPerClass.Collect();

        context.RegisterSourceOutput(
            allMapConfigs,
            static (spc, allConfigsBatch) =>
            {
                var allConfigsWithDiags = allConfigsBatch
                    .SelectMany(tuple =>
                        tuple.Configs.Select(cfg => (tuple.Namespace, Config: cfg))
                    )
                    .ToList();

                foreach (var item in allConfigsWithDiags)
                {
                    foreach (var d in item.Config.Diagnostics)
                    {
                        spc.ReportDiagnostic(d);
                    }
                }

                var allConfigs = allConfigsWithDiags
                    .Select(x => (x.Namespace, x.Config.Config))
                    .ToList();

                if (allConfigs.Count == 0)
                {
                    return;
                }

                var mapPairs = allConfigs
                    .Select(c => (Src: c.Config.SourceSymbol, Dst: c.Config.DestSymbol))
                    .ToList();

                var mapperInfos = new List<MapperInfo>();
                foreach (var cfg in allConfigs)
                {
                    var assignments = BuildAssignments(cfg.Config, mapPairs);
                    mapperInfos.Add(
                        new MapperInfo(
                            Namespace: cfg.Namespace,
                            SourceType: cfg.Config.SourceSymbol.ToDisplayString(
                                SymbolDisplayFormat.FullyQualifiedFormat
                            ),
                            DestType: cfg.Config.DestSymbol.ToDisplayString(
                                SymbolDisplayFormat.FullyQualifiedFormat
                            ),
                            DestShortName: cfg.Config.DestSymbol.Name,
                            Assignments: assignments
                        )
                    );
                }

                var nameSpaceList = new List<string>();
                var code = GenerateCode(mapperInfos, nameSpaceList);
                spc.AddSource("Mappers.g.cs", SourceText.From(code, Encoding.UTF8));

                var usings = new HashSet<string>();
                foreach (var ns in nameSpaceList)
                {
                    if (!string.IsNullOrEmpty(ns))
                    {
                        usings.Add(ns);
                    }
                }

                if (usings.Any())
                {
                    var globalUsingsSource = new StringBuilder();
                    globalUsingsSource.AppendLine("// <auto-generated/> by LinKit.Generator");
                    globalUsingsSource.AppendLine("#nullable enable");
                    globalUsingsSource.AppendLine();
                    foreach (var u in usings)
                    {
                        globalUsingsSource.AppendLine($"global using {u};");
                    }
                    spc.AddSource(
                        "GlobalMapperUsings.g.cs",
                        SourceText.From(globalUsingsSource.ToString(), Encoding.UTF8)
                    );
                }
            }
        );
    }

    private static (List<ForMemberRule> Rules, List<Diagnostic> Diagnostics) CollectForMemberChain(
        InvocationExpressionSyntax createMapCall,
        SemanticModel model
    )
    {
        var rules = new List<ForMemberRule>();
        var diagnostics = new List<Diagnostic>();
        SyntaxNode? current = createMapCall;

        while (
            current?.Parent is MemberAccessExpressionSyntax parentMemberAccess
            && parentMemberAccess.Name.Identifier.Text == "ForMember"
            && parentMemberAccess.Parent is InvocationExpressionSyntax forMemberInvocation
        )
        {
            var (rule, diag) = ParseForMemberInvocation(forMemberInvocation, model);
            if (rule is not null)
            {
                rules.Add(rule);
            }

            if (diag is not null)
            {
                diagnostics.Add(diag);
            }

            current = forMemberInvocation;
        }

        return (rules, diagnostics);
    }

    private static (ForMemberRule? Rule, Diagnostic? Diagnostic) ParseForMemberInvocation(
        InvocationExpressionSyntax invocation,
        SemanticModel model
    )
    {
        var args = invocation.ArgumentList.Arguments;
        if (args.Count != 2)
        {
            return (
                null,
                Diagnostic.Create(
                    InvalidMappingConfiguration,
                    invocation.GetLocation(),
                    "ForMember must have 2 arguments."
                )
            );
        }

        if (
            args[0].Expression is not LambdaExpressionSyntax destLambda
            || destLambda.Body is not MemberAccessExpressionSyntax destMemberAccess
        )
        {
            return (
                null,
                Diagnostic.Create(
                    InvalidMappingConfiguration,
                    args[0].GetLocation(),
                    "Destination member must be a simple property access lambda (e.g., 'd => d.Property')."
                )
            );
        }

        string destMemberName = destMemberAccess.Name.Identifier.ValueText;

        if (
            args[1].Expression is not LambdaExpressionSyntax optionsLambda
            || optionsLambda.Body is not InvocationExpressionSyntax optionInvocation
            || optionInvocation.Expression is not MemberAccessExpressionSyntax optionMemberAccess
        )
        {
            return (
                null,
                Diagnostic.Create(
                    InvalidMappingConfiguration,
                    args[1].GetLocation(),
                    "Mapping option must be a simple method call (e.g., 'opt => opt.MapFrom(...)')."
                )
            );
        }

        var optionMethodName = optionMemberAccess.Name.Identifier.ValueText;

        switch (optionMethodName)
        {
            case "Ignore":
                return (new ForMemberRule(destMemberName, null, Ignore: true), null);
            case "MapFrom":
                if (
                    optionInvocation.ArgumentList.Arguments.Count == 1
                    && optionInvocation.ArgumentList.Arguments[0].Expression
                        is LambdaExpressionSyntax sourceLambda
                )
                {
                    string sourceExpression = LambdaBodyRewriter.Translate(sourceLambda, "source");
                    return (new ForMemberRule(destMemberName, sourceExpression), null);
                }
                break;
            case "ConvertWith":
                return ParseConvertWith(destMemberName, optionInvocation, model);
        }

        return (
            null,
            Diagnostic.Create(
                InvalidMappingConfiguration,
                optionInvocation.GetLocation(),
                $"Unsupported or invalid mapping option '{optionMethodName}'."
            )
        );
    }

    private static (ForMemberRule? Rule, Diagnostic? Diagnostic) ParseConvertWith(
        string destMemberName,
        InvocationExpressionSyntax invocation,
        SemanticModel model
    )
    {
        string? converterTypeDisplay = null;
        string? methodName = null;
        LambdaExpressionSyntax? sourceLambda = null;
        Location? errorLocation = invocation.GetLocation();

        if (invocation.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax gns })
        {
            if (gns.TypeArgumentList.Arguments.Count != 1)
            {
                return (
                    null,
                    Diagnostic.Create(
                        InvalidMappingConfiguration,
                        gns.GetLocation(),
                        "ConvertWith generic method expects 1 type argument."
                    )
                );
            }

            var converterTypeSymbol =
                model.GetTypeInfo(gns.TypeArgumentList.Arguments[0]).Type as INamedTypeSymbol;
            if (converterTypeSymbol is null)
            {
                return (
                    null,
                    Diagnostic.Create(
                        InvalidMappingConfiguration,
                        gns.TypeArgumentList.Arguments[0].GetLocation(),
                        "Could not resolve the converter type."
                    )
                );
            }

            converterTypeDisplay = converterTypeSymbol.ToDisplayString(
                SymbolDisplayFormat.FullyQualifiedFormat
            );

            var args = invocation.ArgumentList.Arguments;
            if (args.Count != 2)
            {
                return (
                    null,
                    Diagnostic.Create(
                        InvalidMappingConfiguration,
                        invocation.GetLocation(),
                        "This ConvertWith overload requires 2 arguments: method name and source lambda."
                    )
                );
            }

            methodName = GetMethodNameFromArgument(args[0]);
            sourceLambda = args[1].Expression as LambdaExpressionSyntax;
            errorLocation = args[0].GetLocation();
        }
        else
        {
            var args = invocation.ArgumentList.Arguments;
            if (args.Count != 3)
            {
                return (
                    null,
                    Diagnostic.Create(
                        InvalidMappingConfiguration,
                        invocation.GetLocation(),
                        "This ConvertWith overload requires 3 arguments: converter type, method name, and source lambda."
                    )
                );
            }

            if (args[0].Expression is TypeOfExpressionSyntax typeOfExpr)
            {
                var converterTypeSymbol =
                    model.GetTypeInfo(typeOfExpr.Type).Type as INamedTypeSymbol;
                if (converterTypeSymbol is null)
                {
                    return (
                        null,
                        Diagnostic.Create(
                            InvalidMappingConfiguration,
                            typeOfExpr.Type.GetLocation(),
                            "Could not resolve the converter type."
                        )
                    );
                }

                converterTypeDisplay = converterTypeSymbol.ToDisplayString(
                    SymbolDisplayFormat.FullyQualifiedFormat
                );
            }
            else
            {
                return (
                    null,
                    Diagnostic.Create(
                        InvalidMappingConfiguration,
                        args[0].GetLocation(),
                        "The first argument must be a typeof() expression."
                    )
                );
            }

            methodName = GetMethodNameFromArgument(args[1]);
            sourceLambda = args[2].Expression as LambdaExpressionSyntax;
            errorLocation = args[1].GetLocation();
        }

        if (methodName is null)
        {
            return (
                null,
                Diagnostic.Create(
                    InvalidMappingConfiguration,
                    errorLocation,
                    "Converter method name must be a string literal or nameof()."
                )
            );
        }

        if (sourceLambda is null)
        {
            return (
                null,
                Diagnostic.Create(
                    InvalidMappingConfiguration,
                    invocation.GetLocation(),
                    "A source member lambda is required."
                )
            );
        }

        string sourceArgument = LambdaBodyRewriter.Translate(sourceLambda, "source");
        string finalExpression = $"{converterTypeDisplay}.{methodName}({sourceArgument})";
        return (new ForMemberRule(destMemberName, finalExpression), null);
    }

    private static string? GetMethodNameFromArgument(ArgumentSyntax arg)
    {
        return arg.Expression switch
        {
            LiteralExpressionSyntax lit when lit.IsKind(SyntaxKind.StringLiteralExpression) =>
                lit.Token.ValueText,
            InvocationExpressionSyntax inv when inv.Expression.ToString() == "nameof" => inv
                .ArgumentList.Arguments.FirstOrDefault()
                ?.Expression switch
            {
                MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.ValueText,
                IdentifierNameSyntax identifierName => identifierName.Identifier.ValueText,
                _ => null,
            },
            _ => null,
        };
    }

    private class LambdaBodyRewriter : CSharpSyntaxRewriter
    {
        private readonly string _oldParameterName;
        private readonly string _newParameterName;

        private LambdaBodyRewriter(string oldParameterName, string newParameterName)
        {
            _oldParameterName = oldParameterName;
            _newParameterName = newParameterName;
        }

        public static string Translate(LambdaExpressionSyntax lambda, string newParameterName)
        {
            var parameter = lambda switch
            {
                SimpleLambdaExpressionSyntax simple => simple.Parameter,
                ParenthesizedLambdaExpressionSyntax parenthesized =>
                    parenthesized.ParameterList.Parameters.FirstOrDefault(),
                _ => null,
            };
            if (parameter == null)
            {
                return lambda.Body.ToString();
            }

            var rewriter = new LambdaBodyRewriter(parameter.Identifier.ValueText, newParameterName);
            return rewriter.Visit(lambda.Body).ToString();
        }

        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
        {
            if (node.Identifier.ValueText == _oldParameterName)
            {
                return SyntaxFactory.IdentifierName(_newParameterName);
            }

            return base.VisitIdentifierName(node);
        }
    }

    private static List<(string DestProp, string SourceExpr)> BuildAssignments(
        MapConfig cfg,
        List<(INamedTypeSymbol Src, INamedTypeSymbol Dst)> allMapPairs
    )
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var configuredProps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rule in cfg.Rules)
        {
            configuredProps.Add(rule.DestinationMember);
            if (rule.Ignore)
            {
                continue;
            }

            if (rule.SourceExpression is not null)
            {
                result[rule.DestinationMember] = rule.SourceExpression;
            }
        }

        var srcProps = GetReadableProps(cfg.SourceSymbol).ToList();
        var dstProps = GetSettableProps(cfg.DestSymbol).ToList();

        foreach (var dp in dstProps)
        {
            if (configuredProps.Contains(dp.Name))
            {
                continue;
            }

            string? assignmentExpr = null;
            var destJsonName = GetJsonPropertyName(dp);
            if (destJsonName != null)
            {
                var spJson = srcProps.FirstOrDefault(s =>
                    string.Equals(
                        GetJsonPropertyName(s),
                        destJsonName,
                        StringComparison.OrdinalIgnoreCase
                    )
                );
                if (spJson != null)
                {
                    TryBuildAssignmentExpression(spJson, dp, allMapPairs, out assignmentExpr);
                }
            }

            if (assignmentExpr == null)
            {
                var spName = srcProps.FirstOrDefault(s =>
                    s.Name.Equals(dp.Name, StringComparison.OrdinalIgnoreCase)
                );
                if (spName != null)
                {
                    TryBuildAssignmentExpression(spName, dp, allMapPairs, out assignmentExpr);
                }
            }

            if (assignmentExpr != null)
            {
                result[dp.Name] = assignmentExpr;
            }
        }

        return result.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    private static bool TryBuildAssignmentExpression(
        IPropertySymbol sp,
        IPropertySymbol dp,
        List<(INamedTypeSymbol Src, INamedTypeSymbol Dst)> allMapPairs,
        out string? expr
    )
    {
        expr = null;

        if (
            sp.Type.SpecialType == SpecialType.System_String
            && dp.Type.SpecialType == SpecialType.System_String
        )
        {
            expr = BuildNullableAwareExpression(sp, dp, $"source.{sp.Name}");
            return true;
        }

        if (AreValueTypesCompatible(sp.Type, dp.Type))
        {
            expr = BuildNullableAwareExpression(sp, dp, $"source.{sp.Name}");
            return true;
        }

        if (SymbolEqualityComparer.Default.Equals(sp.Type, dp.Type))
        {
            expr = BuildNullableAwareExpression(sp, dp, $"source.{sp.Name}");
            return true;
        }

        if (TryBuildCollectionMappingExpr(sp, dp, allMapPairs, out var collExpr))
        {
            expr = collExpr;
            return true;
        }

        if (HasMapping(allMapPairs, sp.Type, dp.Type))
        {
            var destShort = (dp.Type as INamedTypeSymbol)!.Name;
            var nestedExpr = $"source.{sp.Name}?.To{destShort}()";
            expr = BuildNullableAwareExpression(sp, dp, nestedExpr);
            return true;
        }

        return false;
    }

    private static bool AreValueTypesCompatible(ITypeSymbol src, ITypeSymbol dst)
    {
        ITypeSymbol srcUnderlying = src;
        ITypeSymbol dstUnderlying = dst;
        if (
            src is INamedTypeSymbol
            {
                ConstructedFrom.SpecialType: SpecialType.System_Nullable_T
            } ns
        )
        {
            srcUnderlying = ns.TypeArguments[0];
        }

        if (
            dst is INamedTypeSymbol
            {
                ConstructedFrom.SpecialType: SpecialType.System_Nullable_T
            } nd
        )
        {
            dstUnderlying = nd.TypeArguments[0];
        }

        if (!srcUnderlying.IsValueType || !dstUnderlying.IsValueType)
        {
            return false;
        }

        return SymbolEqualityComparer.Default.Equals(srcUnderlying, dstUnderlying);
    }

    private static string BuildNullableAwareExpression(
        IPropertySymbol sourceProp,
        IPropertySymbol destProp,
        string expr
    )
    {
        bool sourceIsNullable = sourceProp.Type.NullableAnnotation == NullableAnnotation.Annotated;
        bool destIsNullable = destProp.Type.NullableAnnotation == NullableAnnotation.Annotated;
        if (sourceIsNullable && !destIsNullable)
        {
            return $"{expr} ?? default";
        }

        return expr;
    }

    private static bool TryBuildCollectionMappingExpr(
        IPropertySymbol sp,
        IPropertySymbol dp,
        List<(INamedTypeSymbol Src, INamedTypeSymbol Dst)> allMapPairs,
        out string? expr
    )
    {
        expr = null;
        if (
            !IsEnumerableType(sp.Type, out var srcItemType)
            || !IsEnumerableType(dp.Type, out var dstItemType)
        )
        {
            return false;
        }

        if (srcItemType is null || dstItemType is null)
        {
            return false;
        }

        if (SymbolEqualityComparer.Default.Equals(srcItemType, dstItemType))
        {
            expr = $"source.{sp.Name}?.ToList()";
            return true;
        }
        if (HasMapping(allMapPairs, srcItemType, dstItemType))
        {
            var dstShort = (dstItemType as INamedTypeSymbol)!.Name;
            expr =
                $"source.{sp.Name}?.Select(x => x.To{dstShort}()).Where(x => x is not null).ToList()!";
            return true;
        }
        return false;
    }

    private static bool IsEnumerableType(ITypeSymbol type, out ITypeSymbol? itemType)
    {
        itemType = null;
        if (type.SpecialType == SpecialType.System_String)
        {
            return false;
        }

        if (
            type.OriginalDefinition.SpecialType
            == SpecialType.System_Collections_Generic_IEnumerable_T
        )
        {
            itemType = ((INamedTypeSymbol)type).TypeArguments[0];
            return true;
        }
        foreach (var iface in type.AllInterfaces)
        {
            if (
                iface.OriginalDefinition.SpecialType
                == SpecialType.System_Collections_Generic_IEnumerable_T
            )
            {
                itemType = iface.TypeArguments[0];
                return true;
            }
        }
        return false;
    }

    private static string? GetJsonPropertyName(ISymbol prop)
    {
        foreach (var attr in prop.GetAttributes())
        {
            var attrClass = attr.AttributeClass?.ToDisplayString();
            if (attrClass == "System.Text.Json.Serialization.JsonPropertyNameAttribute")
            {
                if (
                    attr.ConstructorArguments.Length == 1
                    && attr.ConstructorArguments[0].Value is string s
                )
                {
                    return s;
                }
            }
            if (attrClass == "Newtonsoft.Json.JsonPropertyAttribute")
            {
                if (
                    attr.ConstructorArguments.Length == 1
                    && attr.ConstructorArguments[0].Value is string s
                )
                {
                    return s;
                }

                foreach (var namedArg in attr.NamedArguments)
                {
                    if (namedArg.Key == "PropertyName" && namedArg.Value.Value is string namedValue)
                    {
                        return namedValue;
                    }
                }
            }
        }
        return null;
    }

    private static bool HasMapping(
        List<(INamedTypeSymbol Src, INamedTypeSymbol Dst)> pairs,
        ITypeSymbol? src,
        ITypeSymbol? dst
    )
    {
        if (src is null || dst is null)
        {
            return false;
        }

        if (src is not INamedTypeSymbol s || dst is not INamedTypeSymbol d)
        {
            return false;
        }

        return pairs.Any(p =>
            SymbolEqualityComparer.Default.Equals(p.Src, s)
            && SymbolEqualityComparer.Default.Equals(p.Dst, d)
        );
    }

    private static IEnumerable<IPropertySymbol> GetSettableProps(INamedTypeSymbol type) =>
        type.GetMembers()
            .OfType<IPropertySymbol>()
            .Where(p => p.SetMethod is not null && p.CanBeReferencedByName);

    private static IEnumerable<IPropertySymbol> GetReadableProps(INamedTypeSymbol type) =>
        type.GetMembers()
            .OfType<IPropertySymbol>()
            .Where(p => p.GetMethod is not null && p.CanBeReferencedByName);

    private static string GenerateCode(List<MapperInfo> infos, List<string> nameSpaces)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/> by LinKit.Generator");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Linq;");
        sb.AppendLine();

        var groupedByNamespace = infos.GroupBy(m => m.Namespace);
        foreach (var nsGroup in groupedByNamespace)
        {
            var ns = nsGroup.Key;
            var sanitizedAssemblyName = ns.Replace(".", "_").Replace("-", "_");
            bool hasNamespace = !string.IsNullOrEmpty(ns);
            if (hasNamespace)
            {
                nameSpaces.Add(ns);
                sb.AppendLine($"namespace {ns}");
                sb.AppendLine("{");
            }

            string indent = hasNamespace ? "    " : "";
            sb.AppendLine($"{indent}public static partial class {sanitizedAssemblyName}_MappingExtensions");
            sb.AppendLine($"{indent}{{");

            foreach (var m in nsGroup)
            {
                string methodIndent = indent + "    ";
                sb.AppendLine(
                    $"{methodIndent}public static {m.DestType}? To{m.DestShortName}(this {m.SourceType}? source)"
                );
                sb.AppendLine($"{methodIndent}{{");
                sb.AppendLine($"{methodIndent}    if (source == null) return default;");
                sb.AppendLine($"{methodIndent}    var destination = new {m.DestType}();");
                foreach (var pair in m.Assignments)
                {
                    sb.AppendLine(
                        $"{methodIndent}    destination.{pair.DestProp} = {pair.SourceExpr};"
                    );
                }

                sb.AppendLine($"{methodIndent}    return destination;");
                sb.AppendLine($"{methodIndent}}}");
                sb.AppendLine();
                sb.AppendLine(
                    $"{methodIndent}public static System.Collections.Generic.List<{m.DestType}> To{m.DestShortName}List(this System.Collections.Generic.IEnumerable<{m.SourceType}>? source)"
                );
                sb.AppendLine($"{methodIndent}{{");
                sb.AppendLine(
                    $"{methodIndent}    if (source == null) return new System.Collections.Generic.List<{m.DestType}>();"
                );
                sb.AppendLine(
                    $"{methodIndent}    return source.Select(x => x.To{m.DestShortName}()).Where(x => x != null).ToList()!;"
                );
                sb.AppendLine($"{methodIndent}}}");
                sb.AppendLine();
            }
            sb.AppendLine($"{indent}}}");
            if (hasNamespace)
            {
                sb.AppendLine("}");
            }
        }
        return sb.ToString();
    }
}
