using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using LinKit.Core.Mapping;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace LinKit.Generator.Generators;

internal sealed record ForMemberRule(
    string DestinationMember,
    string? SourceMember,
    string? ConverterTypeDisplay,
    string? ConverterMethod,
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
    private static readonly DiagnosticDescriptor MissingSourceMemberRule = new DiagnosticDescriptor(
        id: "LKM001",
        title: "Source member not found",
        messageFormat: "The source member '{0}' does not exist in type '{1}'",
        category: "Mapping",
        defaultSeverity: DiagnosticSeverity.Warning,
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
                static (tuple, cancellationToken) =>
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
                    var configureMethodSyntax = classSymbol
                        .DeclaringSyntaxReferences.Select(r => r.GetSyntax())
                        .OfType<ClassDeclarationSyntax>()
                        .SelectMany(c => c.Members.OfType<MethodDeclarationSyntax>())
                        .FirstOrDefault(m =>
                            m.Identifier.Text == "Configure"
                            && m.ParameterList.Parameters.Count == 1
                        );
                    if (configureMethodSyntax is null)
                    {
                        configureMethodSyntax = classSyntax
                            .Members.OfType<MethodDeclarationSyntax>()
                            .FirstOrDefault(m =>
                                m.Identifier.Text == "Configure"
                                && m.ParameterList.Parameters.Count == 1
                            );
                    }
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
                            var (rules, diags) = CollectForMemberChain(inv, model, srcType);
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
                var code = GenerateCode(mapperInfos);
                spc.AddSource("Mappers.g.cs", SourceText.From(code, Encoding.UTF8));
            }
        );
    }

    private static (List<ForMemberRule> Rules, List<Diagnostic> Diagnostics) CollectForMemberChain(
        InvocationExpressionSyntax createMapCall,
        SemanticModel model,
        INamedTypeSymbol sourceTypeSymbol
    )
    {
        var rules = new List<ForMemberRule>();
        var diagnostics = new List<Diagnostic>();
        SyntaxNode? current = createMapCall;
        while (true)
        {
            if (
                current.Parent is MemberAccessExpressionSyntax parentMemberAccess
                && parentMemberAccess.Name.Identifier.Text == "ForMember"
            )
            {
                if (parentMemberAccess.Parent is InvocationExpressionSyntax forMemberInvocation)
                {
                    var parseResult = ParseForMemberInvocation(
                        forMemberInvocation,
                        model,
                        sourceTypeSymbol,
                        out var diag
                    );
                    if (parseResult is not null)
                    {
                        rules.Add(parseResult);
                    }
                    if (diag is not null)
                    {
                        diagnostics.Add(diag);
                    }
                    current = forMemberInvocation;
                    continue;
                }
            }
            break;
        }
        return (rules, diagnostics);
    }

    private static ForMemberRule? ParseForMemberInvocation(
        InvocationExpressionSyntax invocation,
        SemanticModel model,
        INamedTypeSymbol sourceTypeSymbol,
        out Diagnostic? diagnostic
    )
    {
        diagnostic = null;
        var args = invocation.ArgumentList.Arguments;
        if (args.Count == 0)
        {
            return null;
        }
        var dest = ParseDestinationMember(args[0].Expression);
        if (string.IsNullOrEmpty(dest))
        {
            return null;
        }
        if (args.Count == 1)
        {
            return new ForMemberRule(dest, null, null, null);
        }
        var second = args[1].Expression;
        if (second is TypeOfExpressionSyntax)
        {
            var conv = ParseConverterStyle(args, model);
            if (conv is not null)
            {
                if (!string.IsNullOrEmpty(conv?.SourceMember))
                {
                    if (!SourceHasMember(sourceTypeSymbol, conv?.SourceMember))
                    {
                        diagnostic = CreateMissingMemberDiagnostic(
                            invocation.ArgumentList.Arguments.ElementAtOrDefault(3)?.GetLocation()
                                ?? invocation.GetLocation(),
                            conv?.SourceMember,
                            sourceTypeSymbol
                        );
                    }
                }
                return new ForMemberRule(
                    dest,
                    conv?.SourceMember,
                    conv?.ConverterTypeDisplay,
                    conv?.ConverterMethod
                );
            }
            return null;
        }
        var src = ParseSourceMemberSimple(second);
        if (!string.IsNullOrEmpty(src))
        {
            if (
                string.Equals(src, MappingRules.Ignore, StringComparison.OrdinalIgnoreCase)
                || string.Equals(src, "Ignore", StringComparison.OrdinalIgnoreCase)
            )
            {
                return new ForMemberRule(dest, null, null, null, true);
            }
            if (!SourceHasMember(sourceTypeSymbol, src))
            {
                diagnostic = CreateMissingMemberDiagnostic(
                    second.GetLocation(),
                    src,
                    sourceTypeSymbol
                );
            }
            return new ForMemberRule(dest, src, null, null);
        }
        return null;
    }

    private static bool SourceHasMember(INamedTypeSymbol sourceTypeSymbol, string memberName)
    {
        if (string.IsNullOrEmpty(memberName))
        {
            return false;
        }
        var prop = sourceTypeSymbol
            .GetMembers()
            .OfType<IPropertySymbol>()
            .FirstOrDefault(p => p.Name.Equals(memberName, StringComparison.OrdinalIgnoreCase));
        if (prop != null)
        {
            return true;
        }
        var field = sourceTypeSymbol
            .GetMembers()
            .OfType<IFieldSymbol>()
            .FirstOrDefault(f => f.Name.Equals(memberName, StringComparison.OrdinalIgnoreCase));
        if (field != null)
        {
            return true;
        }
        return false;
    }

    private static Diagnostic CreateMissingMemberDiagnostic(
        Location location,
        string missingMember,
        INamedTypeSymbol sourceTypeSymbol
    )
    {
        var diag = Diagnostic.Create(
            MissingSourceMemberRule,
            location,
            missingMember,
            sourceTypeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
        );
        return diag;
    }

    private static string? ParseDestinationMember(ExpressionSyntax expr)
    {
        switch (expr)
        {
            case LiteralExpressionSyntax lit when lit.IsKind(SyntaxKind.StringLiteralExpression):
                return lit.Token.ValueText;
            case InvocationExpressionSyntax inv
                when inv.Expression is IdentifierNameSyntax id && id.Identifier.Text == "nameof":
                if (inv.ArgumentList.Arguments.Count == 1)
                {
                    var argExpr = inv.ArgumentList.Arguments[0].Expression;
                    if (argExpr is IdentifierNameSyntax idName)
                    {
                        return idName.Identifier.ValueText;
                    }
                    if (argExpr is MemberAccessExpressionSyntax member)
                    {
                        return member.Name.Identifier.ValueText;
                    }
                }
                break;
            case IdentifierNameSyntax idNameOnly:
                return idNameOnly.Identifier.ValueText;
            case MemberAccessExpressionSyntax ma:
                return ma.Name.Identifier.ValueText;
        }
        return null;
    }

    private static string? ParseSourceMemberSimple(ExpressionSyntax expr)
    {
        switch (expr)
        {
            case LiteralExpressionSyntax lit when lit.IsKind(SyntaxKind.StringLiteralExpression):
                return lit.Token.ValueText;
            case InvocationExpressionSyntax inv
                when inv.Expression is IdentifierNameSyntax id && id.Identifier.Text == "nameof":
                if (inv.ArgumentList.Arguments.Count == 1)
                {
                    var argExpr = inv.ArgumentList.Arguments[0].Expression;
                    if (argExpr is IdentifierNameSyntax idName)
                    {
                        return idName.Identifier.ValueText;
                    }
                    if (argExpr is MemberAccessExpressionSyntax member)
                    {
                        return member.Name.Identifier.ValueText;
                    }
                }
                break;
            case IdentifierNameSyntax idNameOnly:
                return idNameOnly.Identifier.ValueText;
            case MemberAccessExpressionSyntax ma:
                return ma.Name.Identifier.ValueText;
        }
        return null;
    }

    private static (
        string ConverterTypeDisplay,
        string ConverterMethod,
        string? SourceMember
    )? ParseConverterStyle(SeparatedSyntaxList<ArgumentSyntax> args, SemanticModel model)
    {
        if (args.Count < 3)
        {
            return null;
        }
        var typeOfExpr = args[1].Expression as TypeOfExpressionSyntax;
        if (typeOfExpr is null)
        {
            return null;
        }
        var convTypeSymbol = model.GetTypeInfo(typeOfExpr.Type).Type as INamedTypeSymbol;
        var convTypeDisplay = convTypeSymbol?.ToDisplayString(
            SymbolDisplayFormat.FullyQualifiedFormat
        );
        if (string.IsNullOrEmpty(convTypeDisplay))
        {
            return null;
        }
        string? methodName = args.ElementAtOrDefault(2)?.Expression switch
        {
            LiteralExpressionSyntax lit3 when lit3.IsKind(SyntaxKind.StringLiteralExpression) =>
                lit3.Token.ValueText,
            IdentifierNameSyntax idExpr => idExpr.Identifier.ValueText,
            MemberAccessExpressionSyntax maName => maName.ToString(),
            _ => args.ElementAtOrDefault(2)?.GetFirstToken().ValueText,
        };
        string? sourceMember = null;
        if (args.Count > 3)
        {
            var expr3 = args.ElementAtOrDefault(3)?.Expression;
            sourceMember = ParseSourceMemberSimple(expr3!);
            if (!string.IsNullOrEmpty(sourceMember))
            {
                sourceMember = sourceMember?.Trim('"');
            }
        }
        if (string.IsNullOrEmpty(methodName))
        {
            return null;
        }
        return (
            convTypeDisplay!,
            methodName!,
            string.IsNullOrEmpty(sourceMember) ? null : sourceMember
        );
    }

    private static List<(string DestProp, string SourceExpr)> BuildAssignments(
        MapConfig cfg,
        List<(INamedTypeSymbol Src, INamedTypeSymbol Dst)> allMapPairs
    )
    {
        var srcProps = GetReadableProps(cfg.SourceSymbol).ToList();
        var dstProps = GetSettableProps(cfg.DestSymbol).ToList();
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ignoredProps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in cfg.Rules)
        {
            if (
                r.Ignore
                || string.Equals(
                    r.SourceMember,
                    MappingRules.Ignore,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                ignoredProps.Add(r.DestinationMember);
                continue;
            }
            if (r.ConverterTypeDisplay is not null)
            {
                string arg = r.SourceMember is null ? "source" : $"source.{r.SourceMember}";
                result[r.DestinationMember] =
                    $"{r.ConverterTypeDisplay}.{r.ConverterMethod}({arg})";
                continue;
            }
            if (r.SourceMember is not null)
            {
                var sp = srcProps.FirstOrDefault(s =>
                    s.Name.Equals(r.SourceMember, StringComparison.OrdinalIgnoreCase)
                );
                var dp = dstProps.FirstOrDefault(d =>
                    d.Name.Equals(r.DestinationMember, StringComparison.OrdinalIgnoreCase)
                );
                if (dp is null)
                    continue;
                if (sp is null)
                {
                    result[r.DestinationMember] = $"source.{r.SourceMember}";
                    continue;
                }
                if (AreValueTypesCompatible(sp.Type, dp.Type))
                {
                    result[r.DestinationMember] = BuildNullableAwareExpression(
                        sp,
                        dp,
                        $"source.{sp.Name}"
                    );
                }
                else if (TryBuildCollectionMappingExpr(sp, dp, allMapPairs, out var collExpr))
                {
                    result[r.DestinationMember] = collExpr;
                }
                else if (HasMapping(allMapPairs, sp.Type, dp.Type))
                {
                    var destShort = (dp.Type as INamedTypeSymbol)!.Name;
                    var expr = $"source.{sp.Name}?.To{destShort}()";
                    result[r.DestinationMember] = BuildNullableAwareExpression(
                        sp,
                        dp,
                        expr,
                        isProjection: true
                    );
                }
                else if (dp.Type.SpecialType == SpecialType.System_String)
                {
                    result[r.DestinationMember] = $"source.{sp.Name}?.ToString()";
                }
                else
                {
                    result[r.DestinationMember] =
                        $"({dp.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)})source.{sp.Name}";
                }
            }
        }
        foreach (var dp in dstProps)
        {
            if (ignoredProps.Contains(dp.Name) || result.ContainsKey(dp.Name))
            {
                continue;
            }
            var destJson = GetJsonPropertyName(dp);
            if (destJson is not null)
            {
                var sp = srcProps.FirstOrDefault(s =>
                    string.Equals(
                        GetJsonPropertyName(s),
                        destJson,
                        StringComparison.OrdinalIgnoreCase
                    )
                );
                if (sp != null)
                {
                    if (TryBuildAssignmentExpression(sp, dp, allMapPairs, out var jsonExpr))
                    {
                        result[dp.Name] = jsonExpr;
                        continue;
                    }
                }
            }
            var spSameName = srcProps.FirstOrDefault(s =>
                s.Name.Equals(dp.Name, StringComparison.OrdinalIgnoreCase)
            );
            if (spSameName != null)
            {
                if (TryBuildAssignmentExpression(spSameName, dp, allMapPairs, out var nameExpr))
                {
                    result[dp.Name] = nameExpr;
                    continue;
                }
            }
            var spNested = srcProps.FirstOrDefault(s =>
                HasMapping(allMapPairs, s.Type, dp.Type)
                && !result.Values.Any(v => v.Contains($"source.{s.Name}"))
            );
            if (spNested != null)
            {
                if (TryBuildAssignmentExpression(spNested, dp, allMapPairs, out var nestedExpr))
                {
                    result[dp.Name] = nestedExpr;
                    continue;
                }
            }
        }
        return result.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    private static bool AreValueTypesCompatible(ITypeSymbol src, ITypeSymbol dst)
    {
        // Get underlying types for nullable value types
        ITypeSymbol srcUnderlying = src;
        ITypeSymbol dstUnderlying = dst;

        bool srcIsNullable =
            src is INamedTypeSymbol ns
            && ns.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T;
        bool dstIsNullable =
            dst is INamedTypeSymbol nd
            && nd.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T;

        if (srcIsNullable)
            srcUnderlying = ((INamedTypeSymbol)src).TypeArguments[0];
        if (dstIsNullable)
            dstUnderlying = ((INamedTypeSymbol)dst).TypeArguments[0];

        // Check if both are value types or nullable value types
        bool srcIsValueType =
            srcUnderlying.TypeKind == TypeKind.Struct
            || srcUnderlying.SpecialType != SpecialType.None;
        bool dstIsValueType =
            dstUnderlying.TypeKind == TypeKind.Struct
            || dstUnderlying.SpecialType != SpecialType.None;

        if (!srcIsValueType || !dstIsValueType)
        {
            return false;
        }

        // Compare underlying types
        return SymbolEqualityComparer.Default.Equals(srcUnderlying, dstUnderlying);
    }

    private static bool TryBuildAssignmentExpression(
        IPropertySymbol sp,
        IPropertySymbol dp,
        List<(INamedTypeSymbol Src, INamedTypeSymbol Dst)> allMapPairs,
        out string? expr
    )
    {
        expr = "";
        if (AreValueTypesCompatible(sp.Type, dp.Type))
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
            expr = BuildNullableAwareExpression(sp, dp, nestedExpr, isProjection: true);
            return true;
        }
        return false;
    }

    private static string BuildNullableAwareExpression(
        IPropertySymbol sourceProp,
        IPropertySymbol destProp,
        string expr,
        bool isProjection = false
    )
    {
        bool sourceIsNullable =
            sourceProp.Type is INamedTypeSymbol ns
            && ns.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T;
        bool destIsNullable =
            destProp.Type is INamedTypeSymbol nd
            && nd.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T;

        if (sourceIsNullable && !destIsNullable)
        {
            // Map from nullable to non-nullable: use default if null
            return $"{expr} ?? default";
        }
        else if (!sourceIsNullable && destIsNullable)
        {
            // Map from non-nullable to nullable: wrap the value
            return expr;
        }
        else
        {
            // Both nullable or both non-nullable
            return expr;
        }
    }

    private static bool TryBuildCollectionMappingExpr(
        IPropertySymbol sp,
        IPropertySymbol dp,
        List<(INamedTypeSymbol Src, INamedTypeSymbol Dst)> allMapPairs,
        out string? expr
    )
    {
        expr = null;
        if (!IsEnumerableType(sp.Type, out var srcItemType))
        {
            return false;
        }
        if (!IsEnumerableType(dp.Type, out var dstItemType))
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
            expr = $"source.{sp.Name}?.To{dstShort}List()";
            return true;
        }
        return false;
    }

    private static bool IsEnumerableType(ITypeSymbol type, out ITypeSymbol? itemType)
    {
        itemType = null;
        if (type is INamedTypeSymbol named)
        {
            if (named.IsGenericType)
            {
                var constructedFrom = named.ConstructedFrom.ToDisplayString();
                if (
                    constructedFrom.StartsWith(
                        "System.Collections.Generic.List<",
                        StringComparison.Ordinal
                    )
                    || constructedFrom.StartsWith(
                        "System.Collections.Generic.IList<",
                        StringComparison.Ordinal
                    )
                    || constructedFrom.StartsWith(
                        "System.Collections.Generic.IEnumerable<",
                        StringComparison.Ordinal
                    )
                    || constructedFrom.StartsWith(
                        "System.Collections.Generic.ICollection<",
                        StringComparison.Ordinal
                    )
                )
                {
                    itemType = named.TypeArguments[0];
                    return true;
                }
            }
            if (type.TypeKind == TypeKind.Array && type is IArrayTypeSymbol arr)
            {
                itemType = arr.ElementType;
                return true;
            }
        }
        foreach (var @interface in type.AllInterfaces)
        {
            if (@interface.IsGenericType)
            {
                var name = @interface.ConstructedFrom.ToDisplayString();
                if (
                    name.StartsWith(
                        "System.Collections.Generic.IEnumerable<",
                        StringComparison.Ordinal
                    )
                )
                {
                    itemType = @interface.TypeArguments[0];
                    return true;
                }
            }
        }
        return false;
    }

    private static string? GetJsonPropertyName(IPropertySymbol prop)
    {
        foreach (var attr in prop.GetAttributes())
        {
            var attrDisplayString = attr.AttributeClass?.ToDisplayString();
            if (attrDisplayString == "System.Text.Json.Serialization.JsonPropertyNameAttribute")
            {
                if (
                    attr.ConstructorArguments.Length == 1
                    && attr.ConstructorArguments[0].Value is string s
                )
                {
                    return s;
                }
            }
            if (attrDisplayString == "Newtonsoft.Json.JsonPropertyAttribute")
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

    private static IEnumerable<IPropertySymbol> GetSettableProps(INamedTypeSymbol type)
    {
        for (var t = type; t is not null; t = t.BaseType)
        {
            foreach (var m in t.GetMembers().OfType<IPropertySymbol>())
            {
                if (m.SetMethod is not null)
                {
                    yield return m;
                }
            }
        }
    }

    private static IEnumerable<IPropertySymbol> GetReadableProps(INamedTypeSymbol type)
    {
        for (var t = type; t is not null; t = t.BaseType)
        {
            foreach (var m in t.GetMembers().OfType<IPropertySymbol>())
            {
                if (m.GetMethod is not null)
                {
                    yield return m;
                }
            }
        }
    }

    private static string GenerateCode(List<MapperInfo> infos)
    {
        var sb = new StringBuilder();
        int indent = 0;
        void AppendLine(string text = "")
        {
            if (text.Length == 0)
            {
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine(new string(' ', indent * 4) + text);
            }
        }
        sb.AppendLine("// <auto-generated> by LinKit.Generator");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Linq;");
        sb.AppendLine();
        var groupedByNamespace = infos.GroupBy(m => m.Namespace);
        foreach (var nsGroup in groupedByNamespace)
        {
            var ns = nsGroup.Key;
            if (!string.IsNullOrEmpty(ns))
            {
                AppendLine($"namespace {ns}");
                AppendLine("{");
                indent++;
            }
            AppendLine("public static partial class MappingExtensions");
            AppendLine("{");
            indent++;
            foreach (var m in nsGroup)
            {
                AppendLine(
                    $"public static {m.DestType}? To{m.DestShortName}(this {m.SourceType}? source)"
                );
                AppendLine("{");
                indent++;
                AppendLine("if (source == null) return default;");
                AppendLine($"var destination = new {m.DestType}();");
                foreach (var pair in m.Assignments)
                {
                    AppendLine($"destination.{pair.DestProp} = {pair.SourceExpr};");
                }
                AppendLine("return destination;");
                indent--;
                AppendLine("}");
                AppendLine();
                AppendLine(
                    $"public static List<{m.DestType}> To{m.DestShortName}List(this IEnumerable<{m.SourceType}>? source)"
                );
                AppendLine("{");
                indent++;
                AppendLine($"if (source == null) return new List<{m.DestType}>();");
                AppendLine($"var result = new List<{m.DestType}>();");
                AppendLine("foreach (var item in source)");
                AppendLine("{");
                indent++;
                AppendLine($"var mapped = item.To{m.DestShortName}();");
                AppendLine("if (mapped != null) result.Add(mapped);");
                indent--;
                AppendLine("}");
                AppendLine("return result;");
                indent--;
                AppendLine("}");
                AppendLine();
            }
            indent--;
            AppendLine("}");
            if (!string.IsNullOrEmpty(ns))
            {
                indent--;
                AppendLine("}");
            }
        }
        return sb.ToString();
    }
}
