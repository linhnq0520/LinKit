using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace LinKit.Generator.Generators;

internal sealed record ForMemberRule(
    string DestinationMember,
    string? SourceExpression,
    bool Ignore = false,
    HashSet<string>? ExtraNamespaces = null
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
    IReadOnlyList<string> ConstructorArgs,
    IReadOnlyList<(string Name, string Expr)> InitOnlyAssignments,
    IReadOnlyList<(string Name, string Expr)> SettableAssignments,
    HashSet<string> ExtraNamespaces
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
        IncrementalValuesProvider<ClassDeclarationSyntax> mapperContexts =
            context.SyntaxProvider.ForAttributeWithMetadataName(
                MapperContextAttr,
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) => (ClassDeclarationSyntax)ctx.TargetNode
            );

        var mapConfigsPerClass = mapperContexts
            .Combine(context.CompilationProvider)
            .Select(
                static (tuple, _) =>
                {
                    (ClassDeclarationSyntax classSyntax, Compilation compilation) = tuple;
                    SemanticModel model = compilation.GetSemanticModel(classSyntax.SyntaxTree);
                    if (model.GetDeclaredSymbol(classSyntax) is not INamedTypeSymbol classSymbol)
                        return (Namespace: "", Configs: ImmutableArray<MapConfigWithDiags>.Empty);

                    string classNamespace = classSymbol.ContainingNamespace.IsGlobalNamespace
                        ? ""
                        : classSymbol.ContainingNamespace.ToDisplayString();

                    var configureMethodSyntax = classSyntax
                        .Members.OfType<MethodDeclarationSyntax>()
                        .FirstOrDefault(m => m.Identifier.Text == "Configure");

                    if (configureMethodSyntax is null)
                        return (
                            Namespace: classNamespace,
                            Configs: ImmutableArray<MapConfigWithDiags>.Empty
                        );

                    var configsWithDiags = new List<MapConfigWithDiags>();
                    var invocations = configureMethodSyntax
                        .DescendantNodes()
                        .OfType<InvocationExpressionSyntax>();

                    foreach (var inv in invocations)
                    {
                        if (
                            inv.Expression
                                is MemberAccessExpressionSyntax { Name: GenericNameSyntax gns }
                            && gns.Identifier.Text == "CreateMap"
                        )
                        {
                            var typeArgs = gns.TypeArgumentList.Arguments;
                            if (typeArgs.Count != 2)
                                continue;

                            if (
                                model.GetTypeInfo(typeArgs[0]).Type is not INamedTypeSymbol srcType
                                || model.GetTypeInfo(typeArgs[1]).Type
                                    is not INamedTypeSymbol dstType
                            )
                                continue;

                            var (rules, diags) = CollectForMemberChain(inv, model);
                            configsWithDiags.Add(
                                new MapConfigWithDiags(
                                    new MapConfig(srcType, dstType, rules),
                                    diags.ToImmutableArray()
                                )
                            );
                        }
                    }
                    return (
                        Namespace: classNamespace,
                        Configs: configsWithDiags.ToImmutableArray()
                    );
                }
            );

        context.RegisterSourceOutput(
            mapConfigsPerClass.Collect(),
            static (spc, allConfigsBatch) =>
            {
                var allConfigsWithDiags = allConfigsBatch
                    .SelectMany(tuple => tuple.Configs.Select(cfg => (tuple.Namespace, cfg)))
                    .ToList();
                var mapPairs = allConfigsWithDiags
                    .Select(x => (Src: x.cfg.Config.SourceSymbol, Dst: x.cfg.Config.DestSymbol))
                    .ToList();
                var mapperInfos = new List<MapperInfo>();
                var globalNamespaces = new HashSet<string>();

                foreach (var item in allConfigsWithDiags)
                {
                    foreach (var d in item.cfg.Diagnostics)
                        spc.ReportDiagnostic(d);

                    var info = BuildMapperInfo(item.Namespace, item.cfg.Config, mapPairs);
                    mapperInfos.Add(info);
                    if (!string.IsNullOrEmpty(item.Namespace))
                        globalNamespaces.Add(item.Namespace);
                }

                if (mapperInfos.Count > 0)
                {
                    spc.AddSource(
                        "Mappers.g.cs",
                        SourceText.From(GenerateCode(mapperInfos), Encoding.UTF8)
                    );

                    // Gen Global Usings
                    var usings = globalNamespaces.OrderBy(x => x).Select(n => $"global using {n};");
                    var usingsCode = "// <auto-generated/>\n" + string.Join("\n", usings);
                    spc.AddSource(
                        "GlobalMapperUsings.g.cs",
                        SourceText.From(usingsCode, Encoding.UTF8)
                    );
                }
            }
        );
    }

    #region Analysis & Building logic

    private static MapperInfo BuildMapperInfo(
        string ns,
        MapConfig cfg,
        List<(INamedTypeSymbol Src, INamedTypeSymbol Dst)> allMapPairs
    )
    {
        var constructorArgs = new List<string>();
        var initAssignments = new List<(string Name, string Expr)>();
        var settableAssignments = new List<(string Name, string Expr)>();
        var extraNs = new HashSet<string>();

        var rules = cfg.Rules.ToDictionary(
            r => r.DestinationMember,
            StringComparer.OrdinalIgnoreCase
        );
        var srcProps = GetReadableProps(cfg.SourceSymbol).ToList();
        var mappedMembers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Logic Constructor (Greedy)
        var selectedCtor = cfg
            .DestSymbol.Constructors.Where(c =>
                c.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal
            )
            .OrderByDescending(c => c.Parameters.Length)
            .FirstOrDefault();

        if (selectedCtor != null && selectedCtor.Parameters.Length > 0)
        {
            foreach (var param in selectedCtor.Parameters)
            {
                string? expr = null;
                if (rules.TryGetValue(param.Name, out var rule) && rule.SourceExpression != null)
                {
                    expr = rule.SourceExpression;
                    if (rule.ExtraNamespaces != null)
                        foreach (var ens in rule.ExtraNamespaces)
                            extraNs.Add(ens);
                }
                else
                    expr = TryAutoMap(param.Name, param.Type, srcProps, allMapPairs, null);

                constructorArgs.Add(
                    expr
                        ?? $"default({param.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)})!"
                );
                mappedMembers.Add(param.Name);
            }
        }

        // 2. Logic Properties (Init-only vs Settable)
        foreach (var dp in GetSettableProps(cfg.DestSymbol))
        {
            if (mappedMembers.Contains(dp.Name))
                continue;

            string? expr = null;
            if (rules.TryGetValue(dp.Name, out var rule))
            {
                if (rule.Ignore)
                    continue;
                expr = rule.SourceExpression;
                if (rule.ExtraNamespaces != null)
                    foreach (var ens in rule.ExtraNamespaces)
                        extraNs.Add(ens);
            }
            else
                expr = TryAutoMap(dp.Name, dp.Type, srcProps, allMapPairs, dp);

            if (expr != null)
            {
                bool isInitOnly = dp.SetMethod?.IsInitOnly ?? false;
                if (isInitOnly)
                    initAssignments.Add((dp.Name, expr));
                else
                    settableAssignments.Add((dp.Name, expr));
            }
        }

        return new MapperInfo(
            ns,
            cfg.SourceSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            cfg.DestSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            cfg.DestSymbol.Name,
            constructorArgs,
            initAssignments,
            settableAssignments,
            extraNs
        );
    }

    private static string? TryAutoMap(
        string destName,
        ITypeSymbol destType,
        List<IPropertySymbol> srcProps,
        List<(INamedTypeSymbol Src, INamedTypeSymbol Dst)> pairs,
        IPropertySymbol? destProp
    )
    {
        // Match JsonPropertyName
        if (destProp != null)
        {
            var destJson = GetJsonPropertyName(destProp);
            if (destJson != null)
            {
                var sp = srcProps.FirstOrDefault(s =>
                    string.Equals(
                        GetJsonPropertyName(s),
                        destJson,
                        StringComparison.OrdinalIgnoreCase
                    )
                );
                if (
                    sp != null
                    && TryBuildExpr(sp.Type, destType, $"source.{sp.Name}", pairs, out var e)
                )
                    return e;
            }
        }
        // Match Name
        var spName = srcProps.FirstOrDefault(s =>
            s.Name.Equals(destName, StringComparison.OrdinalIgnoreCase)
        );
        if (
            spName != null
            && TryBuildExpr(spName.Type, destType, $"source.{spName.Name}", pairs, out var e2)
        )
            return e2;
        return null;
    }

    private static bool TryBuildExpr(
        ITypeSymbol s,
        ITypeSymbol d,
        string expr,
        List<(INamedTypeSymbol Src, INamedTypeSymbol Dst)> pairs,
        out string? res
    )
    {
        res = null;
        if (SymbolEqualityComparer.Default.Equals(s, d) || AreCompatible(s, d))
        {
            res =
                (
                    s.NullableAnnotation == NullableAnnotation.Annotated
                    && d.NullableAnnotation != NullableAnnotation.Annotated
                )
                    ? $"{expr} ?? default!"
                    : expr;
            return true;
        }
        if (IsEnumerable(s, out var si) && IsEnumerable(d, out var di) && si != null && di != null)
        {
            if (SymbolEqualityComparer.Default.Equals(si, di))
            {
                res = $"{expr}?.ToList()";
                return true;
            }
            if (HasMap(pairs, si, di))
            {
                res = $"{expr}?.Select(x => x.To{di.Name}()).Where(x => x != null).ToList()!";
                return true;
            }
        }
        if (HasMap(pairs, s, d))
        {
            res = $"{expr}?.To{d.Name}()";
            return true;
        }
        return false;
    }

    #endregion

    #region Code Generation

    private static string GenerateCode(List<MapperInfo> infos)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/> by LinKit.Generator");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Linq;");

        foreach (var nsGroup in infos.GroupBy(m => m.Namespace))
        {
            foreach (var m in nsGroup)
            foreach (var ens in m.ExtraNamespaces)
                sb.AppendLine($"using {ens};");

            bool hasNs = !string.IsNullOrEmpty(nsGroup.Key);
            if (hasNs)
                sb.AppendLine($"namespace {nsGroup.Key}\n{{");
            string indent = hasNs ? "    " : "";

            sb.AppendLine(
                $"{indent}public static partial class {nsGroup.Key.Replace(".", "_")}_MappingExtensions"
            );
            sb.AppendLine($"{indent}{{");

            foreach (var m in nsGroup)
            {
                string mi = indent + "    ";
                // --- Single Item Mapper ---
                sb.AppendLine(
                    $"{mi}public static {m.DestType}? To{m.DestShortName}(this {m.SourceType}? source, {m.DestType}? destination = default)"
                );
                sb.AppendLine($"{mi}{{");
                sb.AppendLine($"{mi}    if (source == null) return destination;");
                sb.AppendLine($"{mi}    if (destination == null)");
                sb.AppendLine($"{mi}    {{");

                // Trường hợp tạo mới: Dùng Constructor + Object Initializer (cả init và set)
                string ctorArgs = string.Join(", ", m.ConstructorArgs);
                sb.Append($"{mi}        destination = new {m.DestType}({ctorArgs})");

                var allInitializers = m.InitOnlyAssignments.Concat(m.SettableAssignments).ToList();
                if (allInitializers.Any())
                {
                    sb.AppendLine();
                    sb.AppendLine($"{mi}        {{");
                    foreach (var prop in allInitializers)
                        sb.AppendLine($"{mi}            {prop.Name} = {prop.Expr},");
                    sb.AppendLine($"{mi}        }};");
                }
                else
                    sb.AppendLine(";");

                sb.AppendLine($"{mi}    }}");
                sb.AppendLine($"{mi}    else");
                sb.AppendLine($"{mi}    {{");

                // Trường hợp cập nhật: Chỉ gán các thuộc tính có SET (không gán được INIT)
                if (m.SettableAssignments.Any())
                {
                    foreach (var prop in m.SettableAssignments)
                        sb.AppendLine($"{mi}        destination.{prop.Name} = {prop.Expr};");
                }
                else
                    sb.AppendLine($"{mi}        // No settable properties to update");

                sb.AppendLine($"{mi}    }}");
                sb.AppendLine($"{mi}    return destination;");
                sb.AppendLine($"{mi}}}");
                sb.AppendLine();

                // --- List Mapper ---
                sb.AppendLine(
                    $"{mi}public static List<{m.DestType}> To{m.DestShortName}List(this IEnumerable<{m.SourceType}>? source)"
                );
                sb.AppendLine($"{mi}{{");
                sb.AppendLine($"{mi}    if (source == null) return new List<{m.DestType}>();");
                sb.AppendLine($"{mi}    var destination = new List<{m.DestType}>();");
                sb.AppendLine($"{mi}    foreach (var item in source)");
                sb.AppendLine($"{mi}    {{");
                sb.AppendLine($"{mi}        var mapped = item.To{m.DestShortName}();");
                sb.AppendLine($"{mi}        if (mapped != null) destination.Add(mapped);");
                sb.AppendLine($"{mi}    }}");
                sb.AppendLine($"{mi}    return destination;");
                sb.AppendLine($"{mi}}}");
            }
            sb.AppendLine($"{indent}}}");
            if (hasNs)
                sb.AppendLine("}");
        }
        return sb.ToString();
    }

    #endregion

    #region Parsing Logic (Original)

    private static (List<ForMemberRule> Rules, List<Diagnostic> Diagnostics) CollectForMemberChain(
        InvocationExpressionSyntax createMapCall,
        SemanticModel model
    )
    {
        var rules = new List<ForMemberRule>();
        var diagnostics = new List<Diagnostic>();
        SyntaxNode? current = createMapCall;

        while (
            current?.Parent is MemberAccessExpressionSyntax ma
            && ma.Name.Identifier.Text == "ForMember"
            && ma.Parent is InvocationExpressionSyntax forMemberInv
        )
        {
            var (rule, diag) = ParseForMemberInvocation(forMemberInv, model);
            if (rule != null)
                rules.Add(rule);
            if (diag != null)
                diagnostics.Add(diag);
            current = forMemberInv;
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
            return (
                null,
                Diagnostic.Create(
                    InvalidMappingConfiguration,
                    invocation.GetLocation(),
                    "ForMember needs 2 args."
                )
            );

        if (
            args[0].Expression is not LambdaExpressionSyntax destLambda
            || destLambda.Body is not MemberAccessExpressionSyntax destMa
        )
            return (
                null,
                Diagnostic.Create(
                    InvalidMappingConfiguration,
                    args[0].GetLocation(),
                    "Dest must be a property lambda."
                )
            );

        string destName = destMa.Name.Identifier.ValueText;
        if (
            args[1].Expression is not LambdaExpressionSyntax optLambda
            || optLambda.Body is not InvocationExpressionSyntax optInv
            || optInv.Expression is not MemberAccessExpressionSyntax optMa
        )
            return (
                null,
                Diagnostic.Create(
                    InvalidMappingConfiguration,
                    args[1].GetLocation(),
                    "Invalid mapping option."
                )
            );

        string methodName = optMa.Name.Identifier.ValueText;
        switch (methodName)
        {
            case "Ignore":
                return (new ForMemberRule(destName, null, true), null);
            case "MapFrom":
                if (
                    optInv.ArgumentList.Arguments.Count == 1
                    && optInv.ArgumentList.Arguments[0].Expression
                        is LambdaExpressionSyntax srcLambda
                )
                {
                    var extraNs = new HashSet<string>();
                    CollectRequiredNamespaces(srcLambda.Body, model, extraNs);
                    return (
                        new ForMemberRule(
                            destName,
                            LambdaBodyRewriter.Translate(srcLambda, "source"),
                            false,
                            extraNs
                        ),
                        null
                    );
                }
                break;
            case "ConvertWith":
                return ParseConvertWith(destName, optInv, model);
        }
        return (
            null,
            Diagnostic.Create(
                InvalidMappingConfiguration,
                optInv.GetLocation(),
                $"Unsupported: {methodName}"
            )
        );
    }

    private static (ForMemberRule?, Diagnostic?) ParseConvertWith(
        string destName,
        InvocationExpressionSyntax inv,
        SemanticModel model
    )
    {
        string? convType = null;
        string? method = null;
        LambdaExpressionSyntax? srcLambda = null;

        if (inv.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax gns })
        {
            convType = (
                model.GetTypeInfo(gns.TypeArgumentList.Arguments[0]).Type as INamedTypeSymbol
            )?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            method = GetMethodName(inv.ArgumentList.Arguments[0]);
            srcLambda = inv.ArgumentList.Arguments[1].Expression as LambdaExpressionSyntax;
        }
        else if (inv.ArgumentList.Arguments.Count == 3)
        {
            var typeOf = inv.ArgumentList.Arguments[0].Expression as TypeOfExpressionSyntax;
            convType = (model.GetTypeInfo(typeOf!.Type).Type as INamedTypeSymbol)?.ToDisplayString(
                SymbolDisplayFormat.FullyQualifiedFormat
            );
            method = GetMethodName(inv.ArgumentList.Arguments[1]);
            srcLambda = inv.ArgumentList.Arguments[2].Expression as LambdaExpressionSyntax;
        }

        if (convType != null && method != null && srcLambda != null)
        {
            var extraNs = new HashSet<string>();
            CollectRequiredNamespaces(srcLambda.Body, model, extraNs);
            return (
                new ForMemberRule(
                    destName,
                    $"{convType}.{method}({LambdaBodyRewriter.Translate(srcLambda, "source")})",
                    false,
                    extraNs
                ),
                null
            );
        }
        return (
            null,
            Diagnostic.Create(InvalidMappingConfiguration, inv.GetLocation(), "Invalid ConvertWith")
        );
    }

    private static string? GetMethodName(ArgumentSyntax arg) =>
        arg.Expression switch
        {
            LiteralExpressionSyntax lit => lit.Token.ValueText,
            InvocationExpressionSyntax inv when inv.Expression.ToString() == "nameof" => (
                inv.ArgumentList.Arguments[0].Expression as MemberAccessExpressionSyntax
            )
                ?.Name
                .Identifier
                .ValueText
                ?? (inv.ArgumentList.Arguments[0].Expression as IdentifierNameSyntax)
                    ?.Identifier
                    .ValueText,
            _ => null,
        };

    private static void CollectRequiredNamespaces(
        SyntaxNode node,
        SemanticModel model,
        HashSet<string> ns
    )
    {
        foreach (var desc in node.DescendantNodesAndSelf())
        {
            var sym = model.GetSymbolInfo(desc).Symbol;
            if (sym?.ContainingNamespace != null && !sym.ContainingNamespace.IsGlobalNamespace)
                ns.Add(sym.ContainingNamespace.ToDisplayString());
        }
    }

    private class LambdaBodyRewriter : CSharpSyntaxRewriter
    {
        private readonly string _old,
            _new;

        private LambdaBodyRewriter(string o, string n)
        {
            _old = o;
            _new = n;
        }

        public static string Translate(LambdaExpressionSyntax lambda, string newName)
        {
            var param = lambda switch
            {
                SimpleLambdaExpressionSyntax s => s.Parameter,
                ParenthesizedLambdaExpressionSyntax p =>
                    p.ParameterList.Parameters.FirstOrDefault(),
                _ => null,
            };
            if (param == null)
                return lambda.Body.ToString();
            return new LambdaBodyRewriter(param.Identifier.ValueText, newName)
                .Visit(lambda.Body)
                .ToString();
        }

        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node) =>
            node.Identifier.ValueText == _old
                ? SyntaxFactory.IdentifierName(_new)
                : base.VisitIdentifierName(node);
    }

    #endregion

    #region Reflection Helpers

    private static IEnumerable<IPropertySymbol> GetSettableProps(INamedTypeSymbol t)
    {
        var seen = new HashSet<string>();
        var curr = t;
        while (curr != null)
        {
            foreach (var p in curr.GetMembers().OfType<IPropertySymbol>())
                if (!p.IsStatic && p.SetMethod != null && seen.Add(p.Name))
                    yield return p;
            curr = curr.BaseType;
        }
    }

    private static IEnumerable<IPropertySymbol> GetReadableProps(INamedTypeSymbol t)
    {
        var seen = new HashSet<string>();
        var curr = t;
        while (curr != null)
        {
            foreach (var p in curr.GetMembers().OfType<IPropertySymbol>())
                if (!p.IsStatic && p.GetMethod != null && seen.Add(p.Name))
                    yield return p;
            curr = curr.BaseType;
        }
    }

    private static bool IsEnumerable(ITypeSymbol t, out ITypeSymbol? item)
    {
        item = null;
        if (t.SpecialType == SpecialType.System_String)
            return false;
        var iface = t
            .AllInterfaces.Concat(new[] { t as INamedTypeSymbol })
            .FirstOrDefault(i =>
                i?.OriginalDefinition.SpecialType
                == SpecialType.System_Collections_Generic_IEnumerable_T
            );
        if (iface != null)
        {
            item = iface.TypeArguments[0];
            return true;
        }
        return false;
    }

    private static bool AreCompatible(ITypeSymbol s, ITypeSymbol d)
    {
        var su = (s as INamedTypeSymbol)?.TypeArguments.FirstOrDefault() ?? s;
        var du = (d as INamedTypeSymbol)?.TypeArguments.FirstOrDefault() ?? d;
        return su.IsValueType && du.IsValueType && SymbolEqualityComparer.Default.Equals(su, du);
    }

    private static bool HasMap(
        List<(INamedTypeSymbol Src, INamedTypeSymbol Dst)> pairs,
        ITypeSymbol s,
        ITypeSymbol d
    ) =>
        s is INamedTypeSymbol sn
        && d is INamedTypeSymbol dn
        && pairs.Any(p =>
            SymbolEqualityComparer.Default.Equals(p.Src, sn)
            && SymbolEqualityComparer.Default.Equals(p.Dst, dn)
        );

    private static string? GetJsonPropertyName(ISymbol s) =>
        s.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.Name.Contains("JsonProperty") == true)
            ?.ConstructorArguments.FirstOrDefault()
            .Value?.ToString();

    #endregion
}
