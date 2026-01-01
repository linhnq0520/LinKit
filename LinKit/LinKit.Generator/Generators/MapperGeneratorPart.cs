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
    IReadOnlyList<(string DestProp, string SourceExpr)> MemberAssignments,
    HashSet<string> ExtraNamespaces
);

public static class MapperGeneratorPart
{
    private const string MapperContextAttr = "LinKit.Core.Mapping.MapperContextAttribute";
    private const string MappingProfileAttr = "LinKit.Core.Mapping.MappingProfileAttribute";

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

        IncrementalValuesProvider<(
            string Namespace,
            ImmutableArray<MapConfigWithDiags> Configs
        )> mapConfigsPerClass = mapperContexts
            .Combine(context.CompilationProvider)
            .Select(
                static (tuple, _) =>
                {
                    (ClassDeclarationSyntax classSyntax, Compilation compilation) = tuple;
                    SemanticModel model = compilation.GetSemanticModel(classSyntax.SyntaxTree);
                    if (model.GetDeclaredSymbol(classSyntax) is not INamedTypeSymbol classSymbol)
                    {
                        return (Namespace: "", Configs: ImmutableArray<MapConfigWithDiags>.Empty);
                    }
                    string classNamespace = classSymbol.ContainingNamespace.IsGlobalNamespace
                        ? ""
                        : classSymbol.ContainingNamespace.ToDisplayString();

                    MethodDeclarationSyntax? configureMethodSyntax = classSyntax
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

                    List<MapConfigWithDiags> configsWithDiags = new List<MapConfigWithDiags>();
                    foreach (
                        InvocationExpressionSyntax inv in configureMethodSyntax
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
                            SeparatedSyntaxList<TypeSyntax> typeArgs =
                                gns.TypeArgumentList.Arguments;
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

                            (List<ForMemberRule> rules, List<Diagnostic> diags) =
                                CollectForMemberChain(inv, model);
                            MapConfig cfg = new MapConfig(srcType, dstType, rules);
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

        IncrementalValueProvider<
            ImmutableArray<(string Namespace, ImmutableArray<MapConfigWithDiags> Configs)>
        > allMapConfigs = mapConfigsPerClass.Collect();

        context.RegisterSourceOutput(
            allMapConfigs,
            static (spc, allConfigsBatch) =>
            {
                List<(string Namespace, MapConfigWithDiags Config)> allConfigsWithDiags =
                    allConfigsBatch
                        .SelectMany(tuple =>
                            tuple.Configs.Select(cfg => (tuple.Namespace, Config: cfg))
                        )
                        .ToList();

                foreach ((string Namespace, MapConfigWithDiags Config) item in allConfigsWithDiags)
                {
                    foreach (Diagnostic d in item.Config.Diagnostics)
                    {
                        spc.ReportDiagnostic(d);
                    }
                }

                List<(string Namespace, MapConfig Config)> allConfigs = allConfigsWithDiags
                    .Select(x => (x.Namespace, x.Config.Config))
                    .ToList();
                if (allConfigs.Count == 0)
                {
                    return;
                }

                List<(INamedTypeSymbol Src, INamedTypeSymbol Dst)> mapPairs = allConfigs
                    .Select(c => (Src: c.Config.SourceSymbol, Dst: c.Config.DestSymbol))
                    .ToList();
                List<MapperInfo> mapperInfos = new List<MapperInfo>();
                List<string> nameSpaceList = new List<string>();

                foreach ((string Namespace, MapConfig Config) cfg in allConfigs)
                {
                    (
                        List<string> ctorArgs,
                        List<(string DestProp, string SourceExpr)> assignments,
                        HashSet<string> extraNs
                    ) = BuildAssignments(cfg.Config, mapPairs);
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
                            ConstructorArgs: ctorArgs,
                            MemberAssignments: assignments,
                            ExtraNamespaces: extraNs
                        )
                    );

                    // Thêm namespace của chính mapper vào list global
                    if (!string.IsNullOrEmpty(cfg.Namespace))
                    {
                        nameSpaceList.Add(cfg.Namespace);
                    }
                    // Thêm các namespace phát hiện được từ lambda vào list global
                    // foreach (string ens in extraNs)
                    // {
                    //     nameSpaceList.Add(ens);
                    // }
                }

                string code = GenerateCode(mapperInfos);
                spc.AddSource("Mappers.g.cs", SourceText.From(code, Encoding.UTF8));

                if (nameSpaceList.Any())
                {
                    IOrderedEnumerable<string> uniqueNs = nameSpaceList
                        .Where(n => !string.IsNullOrEmpty(n))
                        .Distinct()
                        .OrderBy(x => x);
                    StringBuilder globalUsingsSource = new StringBuilder();
                    globalUsingsSource.AppendLine("// <auto-generated/> by LinKit.Generator");
                    globalUsingsSource.AppendLine("#nullable enable");
                    foreach (string? u in uniqueNs)
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

    #region Parsing Logic

    private static (List<ForMemberRule> Rules, List<Diagnostic> Diagnostics) CollectForMemberChain(
        InvocationExpressionSyntax createMapCall,
        SemanticModel model
    )
    {
        List<ForMemberRule> rules = new List<ForMemberRule>();
        List<Diagnostic> diagnostics = new List<Diagnostic>();
        SyntaxNode? current = createMapCall;

        while (
            current?.Parent is MemberAccessExpressionSyntax parentMemberAccess
            && parentMemberAccess.Name.Identifier.Text == "ForMember"
            && parentMemberAccess.Parent is InvocationExpressionSyntax forMemberInvocation
        )
        {
            (ForMemberRule? rule, Diagnostic? diag) = ParseForMemberInvocation(
                forMemberInvocation,
                model
            );
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
        SeparatedSyntaxList<ArgumentSyntax> args = invocation.ArgumentList.Arguments;
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
                    "Destination member must be a simple property access lambda."
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
                    "Mapping option must be a simple method call."
                )
            );
        }

        string optionMethodName = optionMemberAccess.Name.Identifier.ValueText;

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

                    HashSet<string> extraNs = new HashSet<string>();
                    CollectRequiredNamespaces(sourceLambda.Body, model, extraNs);

                    return (
                        new ForMemberRule(
                            destMemberName,
                            sourceExpression,
                            ExtraNamespaces: extraNs
                        ),
                        null
                    );
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
                $"Unsupported option '{optionMethodName}'."
            )
        );
    }

    private static void CollectRequiredNamespaces(
        SyntaxNode node,
        SemanticModel model,
        HashSet<string> namespaces
    )
    {
        foreach (SyntaxNode descendant in node.DescendantNodesAndSelf())
        {
            SymbolInfo symbolInfo = model.GetSymbolInfo(descendant);
            ISymbol? symbol = symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();

            if (symbol != null)
            {
                // Nếu là Method (Extension hoặc Static)
                if (symbol is IMethodSymbol method)
                {
                    if (
                        method.ContainingNamespace != null
                        && !method.ContainingNamespace.IsGlobalNamespace
                    )
                    {
                        namespaces.Add(method.ContainingNamespace.ToDisplayString());
                    }
                }
                // Nếu là Type (ví dụ dùng Enum hoặc Static class trong lambda)
                else if (symbol is ITypeSymbol type)
                {
                    if (
                        type.ContainingNamespace != null
                        && !type.ContainingNamespace.IsGlobalNamespace
                    )
                    {
                        namespaces.Add(type.ContainingNamespace.ToDisplayString());
                    }
                }
            }
        }
    }

    private static (ForMemberRule? Rule, Diagnostic? Diagnostic) ParseConvertWith(
        string destMemberName,
        InvocationExpressionSyntax invocation,
        SemanticModel model
    )
    {
        // Giữ nguyên logic cũ của bạn
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
                        "ConvertWith expects 1 type argument."
                    )
                );
            }

            INamedTypeSymbol? converterTypeSymbol =
                model.GetTypeInfo(gns.TypeArgumentList.Arguments[0]).Type as INamedTypeSymbol;
            if (converterTypeSymbol is null)
            {
                return (
                    null,
                    Diagnostic.Create(
                        InvalidMappingConfiguration,
                        gns.GetLocation(),
                        "Could not resolve converter type."
                    )
                );
            }

            converterTypeDisplay = converterTypeSymbol.ToDisplayString(
                SymbolDisplayFormat.FullyQualifiedFormat
            );
            SeparatedSyntaxList<ArgumentSyntax> args = invocation.ArgumentList.Arguments;
            if (args.Count != 2)
            {
                return (
                    null,
                    Diagnostic.Create(
                        InvalidMappingConfiguration,
                        invocation.GetLocation(),
                        "ConvertWith requires method name and source lambda."
                    )
                );
            }

            methodName = GetMethodNameFromArgument(args[0]);
            sourceLambda = args[1].Expression as LambdaExpressionSyntax;
            errorLocation = args[0].GetLocation();
        }
        else
        {
            SeparatedSyntaxList<ArgumentSyntax> args = invocation.ArgumentList.Arguments;
            if (args.Count != 3)
            {
                return (
                    null,
                    Diagnostic.Create(
                        InvalidMappingConfiguration,
                        invocation.GetLocation(),
                        "ConvertWith overload requires 3 arguments."
                    )
                );
            }

            if (args[0].Expression is TypeOfExpressionSyntax typeOfExpr)
            {
                INamedTypeSymbol? converterTypeSymbol =
                    model.GetTypeInfo(typeOfExpr.Type).Type as INamedTypeSymbol;
                if (converterTypeSymbol is null)
                {
                    return (
                        null,
                        Diagnostic.Create(
                            InvalidMappingConfiguration,
                            typeOfExpr.GetLocation(),
                            "Could not resolve converter type."
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
                        "First argument must be typeof()."
                    )
                );
            }

            methodName = GetMethodNameFromArgument(args[1]);
            sourceLambda = args[2].Expression as LambdaExpressionSyntax;
            errorLocation = args[1].GetLocation();
        }

        if (methodName is null || sourceLambda is null)
        {
            return (
                null,
                Diagnostic.Create(
                    InvalidMappingConfiguration,
                    invocation.GetLocation(),
                    "Invalid ConvertWith arguments."
                )
            );
        }

        string sourceArgument = LambdaBodyRewriter.Translate(sourceLambda, "source");
        string finalExpression = $"{converterTypeDisplay}.{methodName}({sourceArgument})";

        HashSet<string> extraNs = new HashSet<string>();
        CollectRequiredNamespaces(sourceLambda.Body, model, extraNs);
        return (new ForMemberRule(destMemberName, finalExpression, ExtraNamespaces: extraNs), null);
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
                MemberAccessExpressionSyntax ma => ma.Name.Identifier.ValueText,
                IdentifierNameSyntax id => id.Identifier.ValueText,
                _ => null,
            },
            _ => null,
        };
    }

    private class LambdaBodyRewriter : CSharpSyntaxRewriter
    {
        private readonly string _oldParameterName;
        private readonly string _newParameterName;

        private LambdaBodyRewriter(string old, string @new)
        {
            _oldParameterName = old;
            _newParameterName = @new;
        }

        public static string Translate(LambdaExpressionSyntax lambda, string newName)
        {
            ParameterSyntax? parameter = lambda switch
            {
                SimpleLambdaExpressionSyntax s => s.Parameter,
                ParenthesizedLambdaExpressionSyntax p =>
                    p.ParameterList.Parameters.FirstOrDefault(),
                _ => null,
            };
            if (parameter == null)
            {
                return lambda.Body.ToString();
            }

            LambdaBodyRewriter rewriter = new LambdaBodyRewriter(
                parameter.Identifier.ValueText,
                newName
            );
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

    #endregion

    #region Mapping Logic (Constructor & Properties)

    private static (
        List<string> ConstructorArgs,
        List<(string DestProp, string SourceExpr)> MemberAssignments,
        HashSet<string> ExtraNamespaces
    ) BuildAssignments(
        MapConfig cfg,
        List<(INamedTypeSymbol Src, INamedTypeSymbol Dst)> allMapPairs
    )
    {
        List<string> constructorArgs = new List<string>();
        Dictionary<string, string> memberAssignments = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase
        );
        HashSet<string> extraNs = new HashSet<string>();

        Dictionary<string, ForMemberRule> configuredRules = cfg.Rules.ToDictionary(
            r => r.DestinationMember,
            StringComparer.OrdinalIgnoreCase
        );
        HashSet<string> mappedDestMembers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<IPropertySymbol> srcProps = GetReadableProps(cfg.SourceSymbol).ToList();

        // 1. Logic Constructor (Greedy)
        List<IMethodSymbol> constructors = cfg
            .DestSymbol.Constructors.Where(c =>
                c.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal
            )
            .OrderByDescending(c => c.Parameters.Length)
            .ToList();

        IMethodSymbol selectedCtor =
            constructors.FirstOrDefault(c => c.Parameters.Length == 0)
            ?? constructors.FirstOrDefault();

        if (selectedCtor != null && selectedCtor.Parameters.Length > 0)
        {
            foreach (IParameterSymbol param in selectedCtor.Parameters)
            {
                string? argExpr = null;
                if (
                    configuredRules.TryGetValue(param.Name, out ForMemberRule? rule)
                    && rule.SourceExpression != null
                )
                {
                    argExpr = rule.SourceExpression;
                    if (rule.ExtraNamespaces != null)
                    {
                        foreach (string ns in rule.ExtraNamespaces)
                        {
                            extraNs.Add(ns);
                        }
                    }
                }
                else
                {
                    argExpr = TryAutoMap(param.Name, param.Type, srcProps, allMapPairs);
                }

                constructorArgs.Add(
                    argExpr
                        ?? $"default({param.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)})"
                );
                mappedDestMembers.Add(param.Name);
            }
        }

        // 2. Logic Properties
        foreach (IPropertySymbol dp in GetSettableProps(cfg.DestSymbol))
        {
            if (configuredRules.TryGetValue(dp.Name, out ForMemberRule? rule))
            {
                if (rule.Ignore)
                {
                    continue;
                }

                if (rule.SourceExpression != null)
                {
                    memberAssignments[dp.Name] = rule.SourceExpression;
                    if (rule.ExtraNamespaces != null)
                    {
                        foreach (string ns in rule.ExtraNamespaces)
                        {
                            extraNs.Add(ns);
                        }
                    }
                }
                continue;
            }
            if (mappedDestMembers.Contains(dp.Name))
            {
                continue;
            }

            string? expr = TryAutoMap(dp.Name, dp.Type, srcProps, allMapPairs, dp);
            if (expr != null)
            {
                memberAssignments[dp.Name] = expr;
            }
        }

        return (
            constructorArgs,
            memberAssignments.Select(kv => (kv.Key, kv.Value)).ToList(),
            extraNs
        );
    }

    private static string? TryAutoMap(
        string destName,
        ITypeSymbol destType,
        List<IPropertySymbol> srcProps,
        List<(INamedTypeSymbol Src, INamedTypeSymbol Dst)> allMapPairs,
        IPropertySymbol? destProp = null
    )
    {
        string? expr = null;
        // Check JsonPropertyName
        if (destProp != null)
        {
            string? destJson = GetJsonPropertyName(destProp);
            if (destJson != null)
            {
                IPropertySymbol sp = srcProps.FirstOrDefault(s =>
                    string.Equals(
                        GetJsonPropertyName(s),
                        destJson,
                        StringComparison.OrdinalIgnoreCase
                    )
                );
                if (
                    sp != null
                    && TryBuildAssignmentExpression(
                        sp.Type,
                        destType,
                        $"source.{sp.Name}",
                        allMapPairs,
                        out expr
                    )
                )
                {
                    return expr;
                }
            }
        }
        // Name match
        IPropertySymbol spName = srcProps.FirstOrDefault(s =>
            s.Name.Equals(destName, StringComparison.OrdinalIgnoreCase)
        );
        if (
            spName != null
            && TryBuildAssignmentExpression(
                spName.Type,
                destType,
                $"source.{spName.Name}",
                allMapPairs,
                out expr
            )
        )
        {
            return expr;
        }

        return null;
    }

    private static bool TryBuildAssignmentExpression(
        ITypeSymbol srcType,
        ITypeSymbol destType,
        string srcExpr,
        List<(INamedTypeSymbol, INamedTypeSymbol)> pairs,
        out string? expr
    )
    {
        expr = null;
        if (
            srcType.SpecialType == SpecialType.System_String
            && destType.SpecialType == SpecialType.System_String
        )
        {
            expr = BuildNullableAware(srcType, destType, srcExpr);
            return true;
        }
        if (AreValueTypesCompatible(srcType, destType))
        {
            expr = BuildNullableAware(srcType, destType, srcExpr);
            return true;
        }
        if (SymbolEqualityComparer.Default.Equals(srcType, destType))
        {
            expr = BuildNullableAware(srcType, destType, srcExpr);
            return true;
        }

        if (TryBuildCollectionMapping(srcType, destType, srcExpr, pairs, out string? coll))
        {
            expr = coll;
            return true;
        }

        if (HasMapping(pairs, srcType, destType))
        {
            string destShort = (destType as INamedTypeSymbol)!.Name;
            expr = BuildNullableAware(srcType, destType, $"{srcExpr}?.To{destShort}()");
            return true;
        }
        return false;
    }

    private static bool TryBuildCollectionMapping(
        ITypeSymbol src,
        ITypeSymbol dst,
        string expr,
        List<(INamedTypeSymbol, INamedTypeSymbol)> pairs,
        out string? res
    )
    {
        res = null;
        if (
            !IsEnumerable(src, out ITypeSymbol? sItem)
            || !IsEnumerable(dst, out ITypeSymbol? dItem)
            || sItem == null
            || dItem == null
        )
        {
            return false;
        }

        if (SymbolEqualityComparer.Default.Equals(sItem, dItem))
        {
            res = $"{expr}?.ToList()";
            return true;
        }
        if (HasMapping(pairs, sItem, dItem))
        {
            string dShort = (dItem as INamedTypeSymbol)!.Name;
            res = $"{expr}?.Select(x => x.To{dShort}()).Where(x => x != null).ToList()!";
            return true;
        }
        return false;
    }

    private static bool IsEnumerable(ITypeSymbol t, out ITypeSymbol? item)
    {
        item = null;
        if (t.SpecialType == SpecialType.System_String)
        {
            return false;
        }

        INamedTypeSymbol? iface =
            t.AllInterfaces.FirstOrDefault(i =>
                i.OriginalDefinition.SpecialType
                == SpecialType.System_Collections_Generic_IEnumerable_T
            )
            ?? (
                t.OriginalDefinition.SpecialType
                == SpecialType.System_Collections_Generic_IEnumerable_T
                    ? t as INamedTypeSymbol
                    : null
            );
        if (iface != null)
        {
            item = iface.TypeArguments[0];
            return true;
        }
        return false;
    }

    private static string BuildNullableAware(ITypeSymbol s, ITypeSymbol d, string e) =>
        (
            s.NullableAnnotation == NullableAnnotation.Annotated
            && d.NullableAnnotation != NullableAnnotation.Annotated
        )
            ? $"{e} ?? default"
            : e;

    private static bool AreValueTypesCompatible(ITypeSymbol s, ITypeSymbol d)
    {
        ITypeSymbol su = (s as INamedTypeSymbol)?.TypeArguments.FirstOrDefault() ?? s;
        ITypeSymbol du = (d as INamedTypeSymbol)?.TypeArguments.FirstOrDefault() ?? d;
        return su.IsValueType && du.IsValueType && SymbolEqualityComparer.Default.Equals(su, du);
    }

    private static string? GetJsonPropertyName(ISymbol s) =>
        s.GetAttributes()
            .FirstOrDefault(a =>
                a.AttributeClass?.ToDisplayString().Contains("JsonProperty") == true
            )
            ?.ConstructorArguments.FirstOrDefault()
            .Value?.ToString();

    private static bool HasMapping(
        List<(INamedTypeSymbol Src, INamedTypeSymbol Dst)> pairs,
        ITypeSymbol? s,
        ITypeSymbol? d
    ) =>
        s is INamedTypeSymbol sn
        && d is INamedTypeSymbol dn
        && pairs.Any(p =>
            SymbolEqualityComparer.Default.Equals(p.Src, sn)
            && SymbolEqualityComparer.Default.Equals(p.Dst, dn)
        );

    private static IEnumerable<IPropertySymbol> GetSettableProps(INamedTypeSymbol t)
    {
        HashSet<string> seen = new HashSet<string>();
        INamedTypeSymbol? current = t;

        while (current != null)
        {
            foreach (IPropertySymbol p in current.GetMembers().OfType<IPropertySymbol>())
            {
                if (!p.IsStatic
                    && p.SetMethod != null
                    && p.SetMethod.DeclaredAccessibility != Accessibility.Private
                    && seen.Add(p.Name)) // Tránh duplicate khi override
                {
                    yield return p;
                }
            }
            current = current.BaseType;
        }
    }

    private static IEnumerable<IPropertySymbol> GetReadableProps(INamedTypeSymbol t)
    {
        HashSet<string> seen = new HashSet<string>();
        INamedTypeSymbol? current = t;

        while (current != null)
        {
            foreach (IPropertySymbol p in current.GetMembers().OfType<IPropertySymbol>())
            {
                if (!p.IsStatic
                    && p.GetMethod != null
                    && seen.Add(p.Name)) // Tránh duplicate khi override
                {
                    yield return p;
                }
            }
            current = current.BaseType;
        }
    }

    #endregion

    #region Code Generation

    private static string GenerateCode(List<MapperInfo> infos)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/> by LinKit.Generator");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Linq;");

        foreach (IGrouping<string, MapperInfo>? nsGroup in infos.GroupBy(m => m.Namespace))
        {
            foreach (MapperInfo? m in nsGroup)
            {
                foreach (string extra in m.ExtraNamespaces)
                {
                    sb.AppendLine($"using {extra};");
                }
            }
            sb.AppendLine();
            string ns = nsGroup.Key;
            string sanitizedName = ns.Replace(".", "_").Replace("-", "_");
            bool hasNs = !string.IsNullOrEmpty(ns);
            if (hasNs)
            {
                sb.AppendLine($"namespace {ns}\n{{");
            }
            string indent = hasNs ? "    " : "";

            sb.AppendLine($"{indent}public static partial class {sanitizedName}_MappingExtensions");
            sb.AppendLine($"{indent}{{");

            foreach (MapperInfo? m in nsGroup)
            {
                string mi = indent + "    ";
                sb.AppendLine(
                    $"{mi}public static {m.DestType}? To{m.DestShortName}(this {m.SourceType}? source)"
                );
                sb.AppendLine($"{mi}{{");
                sb.AppendLine($"{mi}    if (source == null) return default;");

                string argsStr = string.Join(", ", m.ConstructorArgs);
                sb.Append($"{mi}    return new {m.DestType}({argsStr})");

                if (m.MemberAssignments.Any())
                {
                    sb.AppendLine();
                    sb.AppendLine($"{mi}    {{");
                    foreach ((string DestProp, string SourceExpr) pair in m.MemberAssignments)
                    {
                        sb.AppendLine($"{mi}        {pair.DestProp} = {pair.SourceExpr},");
                    }

                    sb.AppendLine($"{mi}    }};");
                }
                else
                {
                    sb.AppendLine(";");
                }

                sb.AppendLine($"{mi}}}");
                sb.AppendLine();

                // List Extension
                sb.AppendLine(
                    $"{mi}public static List<{m.DestType}> To{m.DestShortName}List(this IEnumerable<{m.SourceType}>? source)"
                );
                sb.AppendLine($"{mi}{{");
                sb.AppendLine($"{mi}    if (source == null) return new List<{m.DestType}>();");
                sb.AppendLine(
                    $"{mi}    return source.Select(x => x.To{m.DestShortName}()).Where(x => x != null).ToList()!;"
                );
                sb.AppendLine($"{mi}}}");
                sb.AppendLine();
            }

            sb.AppendLine($"{indent}}}");
            if (hasNs)
            {
                sb.AppendLine("}");
            }
        }
        return sb.ToString();
    }

    #endregion
}
