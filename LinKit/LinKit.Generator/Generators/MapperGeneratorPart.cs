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
    IReadOnlyList<string> ConstructorArgs,
    IReadOnlyList<(string DestProp, string SourceExpr)> MemberAssignments
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
        var mapperContexts = context.SyntaxProvider.ForAttributeWithMetadataName(
            MapperContextAttr,
            static (node, _) => node is ClassDeclarationSyntax c,
            static (ctx, _) => (ClassDeclarationSyntax)ctx.TargetNode
        );
        var mappingProfile = context.SyntaxProvider.ForAttributeWithMetadataName(
            MappingProfileAttr,
            static (node, _) => node is ClassDeclarationSyntax c,
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
                    var (ctorArgs, assignments) = BuildAssignments(cfg.Config, mapPairs);
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
                            MemberAssignments: assignments
                        )
                    );
                }

                var nameSpaceList = new List<string>();
                var code = GenerateCode(mapperInfos, nameSpaceList);
                spc.AddSource("Mappers.g.cs", SourceText.From(code, Encoding.UTF8));

                if (nameSpaceList.Any(ns => !string.IsNullOrEmpty(ns)))
                {
                    var uniqueNs = nameSpaceList.Where(n => !string.IsNullOrEmpty(n)).Distinct();
                    var globalUsingsSource = new StringBuilder();
                    globalUsingsSource.AppendLine("// <auto-generated/> by LinKit.Generator");
                    globalUsingsSource.AppendLine("#nullable enable");
                    foreach (var u in uniqueNs)
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
                $"Unsupported option '{optionMethodName}'."
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
                        "ConvertWith expects 1 type argument."
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
                        gns.GetLocation(),
                        "Could not resolve converter type."
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
            var args = invocation.ArgumentList.Arguments;
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
                var converterTypeSymbol =
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

        if (methodName is null)
        {
            return (
                null,
                Diagnostic.Create(
                    InvalidMappingConfiguration,
                    errorLocation,
                    "Method name must be string or nameof()."
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
                    "Source lambda required."
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

    #endregion

    #region Mapping Logic (Updated for Constructors)

    private static (
        List<string> ConstructorArgs,
        List<(string DestProp, string SourceExpr)> MemberAssignments
    ) BuildAssignments(
        MapConfig cfg,
        List<(INamedTypeSymbol Src, INamedTypeSymbol Dst)> allMapPairs
    )
    {
        var constructorArgs = new List<string>();
        var memberAssignments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var configuredRules = cfg.Rules.ToDictionary(
            r => r.DestinationMember,
            StringComparer.OrdinalIgnoreCase
        );
        var mappedDestMembers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var srcProps = GetReadableProps(cfg.SourceSymbol).ToList();

        IMethodSymbol? selectedCtor = null;
        var constructors = cfg
            .DestSymbol.Constructors.Where(c =>
                c.DeclaredAccessibility == Accessibility.Public
                || c.DeclaredAccessibility == Accessibility.Internal
            )
            .OrderByDescending(c => c.Parameters.Length)
            .ToList();

        var parameterlessCtor = constructors.FirstOrDefault(c => c.Parameters.Length == 0);

        if (parameterlessCtor != null)
        {
            selectedCtor = parameterlessCtor;
        }
        else if (constructors.Count > 0)
        {
            selectedCtor = constructors.First();
        }

        if (selectedCtor != null && selectedCtor.Parameters.Length > 0)
        {
            foreach (var param in selectedCtor.Parameters)
            {
                string paramName = param.Name;
                string? argExpr = null;

                if (
                    configuredRules.TryGetValue(paramName, out var rule)
                    && rule.SourceExpression != null
                )
                {
                    argExpr = rule.SourceExpression;
                    mappedDestMembers.Add(paramName);
                }
                else
                {
                    argExpr = TryAutoMap(paramName, param.Type, srcProps, allMapPairs);
                }

                if (argExpr != null)
                {
                    constructorArgs.Add(argExpr);
                }
                else
                {
                    constructorArgs.Add(
                        $"default({param.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)})"
                    );
                }

                mappedDestMembers.Add(paramName);
            }
        }

        var settableProps = GetSettableProps(cfg.DestSymbol);
        foreach (var dp in settableProps)
        {
            if (configuredRules.TryGetValue(dp.Name, out var rule))
            {
                if (rule.Ignore)
                {
                    continue;
                }

                if (rule.SourceExpression != null)
                {
                    memberAssignments[dp.Name] = rule.SourceExpression;
                }
                continue;
            }

            if (mappedDestMembers.Contains(dp.Name))
            {
                continue;
            }

            var expr = TryAutoMap(dp.Name, dp.Type, srcProps, allMapPairs, dp);
            if (expr != null)
            {
                memberAssignments[dp.Name] = expr;
            }
        }

        return (constructorArgs, memberAssignments.Select(kv => (kv.Key, kv.Value)).ToList());
    }

    private static string? TryAutoMap(
        string destName,
        ITypeSymbol destType,
        List<IPropertySymbol> srcProps,
        List<(INamedTypeSymbol Src, INamedTypeSymbol Dst)> allMapPairs,
        IPropertySymbol? destPropSymbol = null
    )
    {
        string? assignmentExpr = null;

        if (destPropSymbol != null)
        {
            var destJsonName = GetJsonPropertyName(destPropSymbol);
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
                    if (
                        TryBuildAssignmentExpression(
                            spJson.Type,
                            destType,
                            $"source.{spJson.Name}",
                            allMapPairs,
                            out assignmentExpr
                        )
                    )
                    {
                        return assignmentExpr;
                    }
                }
            }
        }

        var spName = srcProps.FirstOrDefault(s =>
            s.Name.Equals(destName, StringComparison.OrdinalIgnoreCase)
        );
        if (spName != null)
        {
            if (
                TryBuildAssignmentExpression(
                    spName.Type,
                    destType,
                    $"source.{spName.Name}",
                    allMapPairs,
                    out assignmentExpr
                )
            )
            {
                return assignmentExpr;
            }
        }

        return null;
    }

    private static bool TryBuildAssignmentExpression(
        ITypeSymbol srcType,
        ITypeSymbol destType,
        string srcExpr,
        List<(INamedTypeSymbol Src, INamedTypeSymbol Dst)> allMapPairs,
        out string? expr
    )
    {
        expr = null;

        // String
        if (
            srcType.SpecialType == SpecialType.System_String
            && destType.SpecialType == SpecialType.System_String
        )
        {
            expr = BuildNullableAwareExpression(srcType, destType, srcExpr);
            return true;
        }

        // Value Types
        if (AreValueTypesCompatible(srcType, destType))
        {
            expr = BuildNullableAwareExpression(srcType, destType, srcExpr);
            return true;
        }

        // Same Type
        if (SymbolEqualityComparer.Default.Equals(srcType, destType))
        {
            expr = BuildNullableAwareExpression(srcType, destType, srcExpr);
            return true;
        }

        // Collection
        if (
            TryBuildCollectionMappingExpr(srcType, destType, srcExpr, allMapPairs, out var collExpr)
        )
        {
            expr = collExpr;
            return true;
        }

        // Nested Object
        if (HasMapping(allMapPairs, srcType, destType))
        {
            var destShort = (destType as INamedTypeSymbol)!.Name;
            var nestedExpr = $"{srcExpr}?.To{destShort}()";
            expr = BuildNullableAwareExpression(srcType, destType, nestedExpr);
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
        ITypeSymbol srcType,
        ITypeSymbol destType,
        string expr
    )
    {
        bool sourceIsNullable = srcType.NullableAnnotation == NullableAnnotation.Annotated;
        bool destIsNullable = destType.NullableAnnotation == NullableAnnotation.Annotated;

        if (sourceIsNullable && !destIsNullable)
        {
            return $"{expr} ?? default";
        }
        return expr;
    }

    private static bool TryBuildCollectionMappingExpr(
        ITypeSymbol srcType,
        ITypeSymbol destType,
        string srcExpr,
        List<(INamedTypeSymbol Src, INamedTypeSymbol Dst)> allMapPairs,
        out string? expr
    )
    {
        expr = null;
        if (
            !IsEnumerableType(srcType, out var srcItemType)
            || !IsEnumerableType(destType, out var dstItemType)
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
            expr = $"{srcExpr}?.ToList()";
            return true;
        }

        if (HasMapping(allMapPairs, srcItemType, dstItemType))
        {
            var dstShort = (dstItemType as INamedTypeSymbol)!.Name;
            expr = $"{srcExpr}?.Select(x => x.To{dstShort}()).Where(x => x is not null).ToList()!";
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
            if (
                attrClass == "System.Text.Json.Serialization.JsonPropertyNameAttribute"
                || attrClass == "Newtonsoft.Json.JsonPropertyAttribute"
            )
            {
                if (
                    attr.ConstructorArguments.Length > 0
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
            .Where(p =>
                !p.IsStatic
                && p.SetMethod is not null
                && p.SetMethod.DeclaredAccessibility != Accessibility.Private
                && p.CanBeReferencedByName
            );

    private static IEnumerable<IPropertySymbol> GetReadableProps(INamedTypeSymbol type) =>
        type.GetMembers()
            .OfType<IPropertySymbol>()
            .Where(p => !p.IsStatic && p.GetMethod is not null && p.CanBeReferencedByName);

    #endregion

    #region Code Generation

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
            sb.AppendLine(
                $"{indent}public static partial class {sanitizedAssemblyName}_MappingExtensions"
            );
            sb.AppendLine($"{indent}{{");

            foreach (var m in nsGroup)
            {
                string methodIndent = indent + "    ";

                sb.AppendLine(
                    $"{methodIndent}public static {m.DestType}? To{m.DestShortName}(this {m.SourceType}? source)"
                );
                sb.AppendLine($"{methodIndent}{{");
                sb.AppendLine($"{methodIndent}    if (source == null) return default;");

                string argsStr = m.ConstructorArgs.Any()
                    ? string.Join(", ", m.ConstructorArgs)
                    : "";

                sb.Append($"{methodIndent}    return new {m.DestType}({argsStr})");

                if (m.MemberAssignments.Any())
                {
                    sb.AppendLine();
                    sb.AppendLine($"{methodIndent}    {{");
                    foreach (var pair in m.MemberAssignments)
                    {
                        sb.AppendLine(
                            $"{methodIndent}        {pair.DestProp} = {pair.SourceExpr},"
                        );
                    }
                    sb.AppendLine($"{methodIndent}    }};");
                }
                else
                {
                    sb.AppendLine(";");
                }

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

    #endregion
}
