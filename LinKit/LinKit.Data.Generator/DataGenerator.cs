using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace LinKit.Data.Generator;

[Generator]
public class DataGenerator : IIncrementalGenerator
{
    private static readonly SymbolDisplayFormat FullyQualifiedNullableFormat =
        SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions
            | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    private static readonly DiagnosticDescriptor MultiTenantFilterNotEnforced = new(
        id: "LNKDATA001",
        title: "Multi-tenant filtering is not enforced",
        messageFormat: "Entity '{0}' uses [MultiTenant], but LinKit.Data does not automatically scope repository queries by tenant. Add explicit tenant predicates; do not rely on this attribute for data isolation.",
        category: "LinKit.Data",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The MultiTenant attribute is metadata only until tenant-aware repository filtering is implemented.");

    private static readonly DiagnosticDescriptor PrimaryKeyFallback = new(
        id: "LNKDATA002",
        title: "No primary key was identified",
        messageFormat: "Entity '{0}' has no [PrimaryKey], Id, or entity-name-plus-Id property. Repository generation uses the first mapped property as its key when available; otherwise it falls back to Guid Id.",
        category: "LinKit.Data",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Declare an explicit or conventional primary key to avoid generating repository operations against an arbitrary property.");

    private static readonly DiagnosticDescriptor SoftDeleteColumnNotMapped = new(
        id: "LNKDATA003",
        title: "Soft-delete column is not mapped",
        messageFormat: "Entity '{0}' uses [SoftDelete(\"{1}\")], but no mapped property has that column name. Schema synchronization will not create the soft-delete column.",
        category: "LinKit.Data",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Map the soft-delete column as an entity property or add it through a database migration.");

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // 1. Discover [Table] classes
        var entityDeclarations = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (s, _) => s is ClassDeclarationSyntax c && c.AttributeLists.Count > 0,
                transform: static (ctx, _) => GetEntitySemanticModel(ctx)
            )
            .Where(static m => m is not null);

        // 2. Discover [DataRepository] interfaces
        var repoDeclarations = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (s, _) =>
                    s is InterfaceDeclarationSyntax i &&
                    (i.AttributeLists.Count > 0 || i.Identifier.ValueText.EndsWith("Repository", StringComparison.Ordinal)),
                transform: static (ctx, _) => GetRepositorySemanticModel(ctx)
            )
            .Where(static m => m is not null);

        // 3. Discover [DataMigration] classes
        var migrationDeclarations = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (s, _) => s is ClassDeclarationSyntax c && c.AttributeLists.Count > 0,
                transform: static (ctx, _) => GetMigrationSemanticModel(ctx)
            )
            .Where(static m => m is not null);

        var rootNamespace = context.AnalyzerConfigOptionsProvider
            .Select(static (provider, _) =>
                provider.GlobalOptions.TryGetValue("build_property.RootNamespace", out var value)
                    ? value
                    : string.Empty);

        var combined = entityDeclarations.Collect()
            .Combine(repoDeclarations.Collect())
            .Combine(migrationDeclarations.Collect())
            .Combine(rootNamespace);

        context.RegisterSourceOutput(combined, static (spc, source) =>
        {
            var entities = source.Left.Left.Left.Where(e => e != null).Select(e => e!).ToList();
            var repos = source.Left.Left.Right.Where(r => r != null).Select(r => r!).ToList();
            var migrations = source.Left.Right.Where(m => m != null).Select(m => m!).ToList();
            var rootNamespaceValue = string.IsNullOrWhiteSpace(source.Right)
                ? entities.FirstOrDefault()?.AssemblyName
                    ?? migrations.FirstOrDefault()?.AssemblyName
                    ?? string.Empty
                : source.Right;
            var generatedNamespace = string.IsNullOrWhiteSpace(rootNamespaceValue)
                ? "Generated"
                : $"{rootNamespaceValue}.Generated";

            foreach (var entity in entities)
            {
                if (entity.RepositoryInterfaceName is not null &&
                    string.IsNullOrWhiteSpace(entity.RepositoryInterfaceNamespace))
                    entity.RepositoryInterfaceNamespace = rootNamespaceValue;
            }

            foreach (var entity in entities)
            {
                if (entity.TenantColumn is not null && entity.Location is not null)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(
                        MultiTenantFilterNotEnforced,
                        entity.Location,
                        entity.FullName));
                }

                if (entity.HasFallbackPrimaryKey && entity.Location is not null)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(
                        PrimaryKeyFallback,
                        entity.Location,
                        entity.FullName));
                }

                if (entity.SoftDeleteColumn is not null
                    && entity.Location is not null
                    && !entity.Properties.Any(property =>
                        string.Equals(property.ColumnName, entity.SoftDeleteColumn, StringComparison.OrdinalIgnoreCase)))
                {
                    spc.ReportDiagnostic(Diagnostic.Create(
                        SoftDeleteColumnNotMapped,
                        entity.Location,
                        entity.FullName,
                        entity.SoftDeleteColumn));
                }
            }

            if (entities.Count == 0 && migrations.Count == 0)
            {
                return;
            }

            if (entities.Count > 0)
            {
                // Generate Mappers
                var mappersSrc = GenerateMappers(entities, generatedNamespace);
                spc.AddSource("LinKit.Data.Mappers.g.cs", SourceText.From(mappersSrc, Encoding.UTF8));

                // Generate Schema Provider
                var schemaSrc = GenerateSchemaProvider(entities, generatedNamespace);
                spc.AddSource("LinKit.Data.SchemaProvider.g.cs", SourceText.From(schemaSrc, Encoding.UTF8));

                var repositoryInterfacesSrc = GenerateRepositoryInterfaces(entities);
                if (entities.Any(entity => entity.RepositoryInterfaceFullName is not null))
                    spc.AddSource("LinKit.Data.RepositoryContracts.g.cs", SourceText.From(repositoryInterfacesSrc, Encoding.UTF8));

                // Generate Repositories
                var reposSrc = GenerateRepositories(entities, repos, rootNamespaceValue);
                spc.AddSource("LinKit.Data.Repositories.g.cs", SourceText.From(reposSrc, Encoding.UTF8));
            }

            if (migrations.Count > 0)
            {
                // Generate Migration Registry
                var migrationsSrc = GenerateMigrationsRegistry(migrations, generatedNamespace);
                spc.AddSource("LinKit.Data.Migrations.g.cs", SourceText.From(migrationsSrc, Encoding.UTF8));
            }

            // Generate Dependency Injection
            var diSrc = GenerateDependencyInjection(entities, repos, migrations, rootNamespaceValue, generatedNamespace);
            spc.AddSource("LinKit.Data.DependencyInjection.g.cs", SourceText.From(diSrc, Encoding.UTF8));
        });
    }

    private static MigrationModel? GetMigrationSemanticModel(GeneratorSyntaxContext ctx)
    {
        var classDecl = (ClassDeclarationSyntax)ctx.Node;
        var symbol = ctx.SemanticModel.GetDeclaredSymbol(classDecl);
        if (symbol == null) return null;

        var migrationAttr = symbol.GetAttributes().FirstOrDefault(a =>
            a.AttributeClass?.Name is "MigrationAttribute" or "Migration" or "DataMigrationAttribute" or "DataMigration" ||
            a.AttributeClass?.ToDisplayString() is "LinKit.Data.MigrationAttribute" or "LinKit.Data.DataMigrationAttribute");

        if (migrationAttr == null) return null;

        string version = "";
        string description = "";

        if (migrationAttr.ConstructorArguments.Length > 0 && migrationAttr.ConstructorArguments[0].Value is string v)
        {
            version = v;
        }

        foreach (var named in migrationAttr.NamedArguments)
        {
            if (named.Key == "Version" && named.Value.Value is string ver)
                version = ver;
            if (named.Key == "Description" && named.Value.Value is string desc)
                description = desc;
        }

        if (string.IsNullOrWhiteSpace(version)) return null;

        return new MigrationModel
        {
            ClassFullName = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            ClassName = symbol.Name,
            AssemblyName = symbol.ContainingAssembly.Name,
            Version = version,
            Description = description
        };
    }

    private static EntityModel? GetEntitySemanticModel(GeneratorSyntaxContext ctx)
    {
        var classDecl = (ClassDeclarationSyntax)ctx.Node;
        var symbol = ctx.SemanticModel.GetDeclaredSymbol(classDecl);
        if (symbol is not INamedTypeSymbol classSymbol) return null;

        var tableAttr = classSymbol.GetAttributes().FirstOrDefault(a =>
            a.AttributeClass?.Name == "TableAttribute" ||
            a.AttributeClass?.ToDisplayString() == "LinKit.Data.TableAttribute");

        if (tableAttr == null) return null;

        string tableName = classSymbol.Name;
        if (tableAttr.ConstructorArguments.Length > 0 && tableAttr.ConstructorArguments[0].Value is string customName && !string.IsNullOrWhiteSpace(customName))
        {
            tableName = customName;
        }

        string? schema = null;
        var schemaNamed = tableAttr.NamedArguments.FirstOrDefault(kv => kv.Key == "Schema");
        if (schemaNamed.Value.Value is string sVal && !string.IsNullOrWhiteSpace(sVal))
        {
            schema = sVal;
        }

        string? tableComment = null;
        var commentNamed = tableAttr.NamedArguments.FirstOrDefault(kv => kv.Key == "Comment");
        if (commentNamed.Value.Value is string cVal && !string.IsNullOrWhiteSpace(cVal))
        {
            tableComment = cVal;
        }
        else
        {
            var commentAttr = classSymbol.GetAttributes().FirstOrDefault(a =>
                a.AttributeClass?.Name == "CommentAttribute" ||
                a.AttributeClass?.ToDisplayString() == "LinKit.Data.CommentAttribute");
            if (commentAttr != null && commentAttr.ConstructorArguments.Length > 0 && commentAttr.ConstructorArguments[0].Value is string tcVal)
            {
                tableComment = tcVal;
            }
        }

        var checkConstraints = new List<CheckConstraintModel>();
        foreach (var chkAttr in classSymbol.GetAttributes().Where(a =>
            a.AttributeClass?.Name == "CheckConstraintAttribute" ||
            a.AttributeClass?.ToDisplayString() == "LinKit.Data.CheckConstraintAttribute"))
        {
            if (chkAttr.ConstructorArguments.Length > 0 && chkAttr.ConstructorArguments[0].Value is string sqlExpr)
            {
                string? chkName = null;
                var nArg = chkAttr.NamedArguments.FirstOrDefault(k => k.Key == "Name");
                if (nArg.Value.Value is string nameVal) chkName = nameVal;
                checkConstraints.Add(new CheckConstraintModel { SqlExpression = sqlExpr, Name = chkName });
            }
        }

        var compositeIndexes = new List<CompositeIndexModel>();
        foreach (var cIdxAttr in classSymbol.GetAttributes().Where(a =>
            a.AttributeClass?.Name == "CompositeIndexAttribute" ||
            a.AttributeClass?.ToDisplayString() == "LinKit.Data.CompositeIndexAttribute"))
        {
            if (cIdxAttr.ConstructorArguments.Length >= 2 &&
                cIdxAttr.ConstructorArguments[0].Value is string idxName &&
                cIdxAttr.ConstructorArguments[1].Values is var colConstants)
            {
                var colList = colConstants.Select(c => c.Value?.ToString()).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToList();
                bool isUnique = false;
                var uNamed = cIdxAttr.NamedArguments.FirstOrDefault(k => k.Key == "IsUnique");
                if (uNamed.Value.Value is bool uVal) isUnique = uVal;
                compositeIndexes.Add(new CompositeIndexModel { IndexName = idxName, ColumnNames = colList, IsUnique = isUnique });
            }

        }

        var repositoryAttribute = classSymbol.GetAttributes().FirstOrDefault(a =>
            a.AttributeClass?.Name == "DataRepositoryAttribute" ||
            a.AttributeClass?.ToDisplayString() == "LinKit.Data.DataRepositoryAttribute");
        string? repositoryInterfaceName = $"I{classSymbol.Name}Repository";
        string? repositoryInterfaceNamespace = null;
        if (repositoryAttribute != null)
        {
            var configuredInterfaceName = repositoryAttribute.ConstructorArguments.FirstOrDefault().Value as string;
            if (!string.IsNullOrWhiteSpace(configuredInterfaceName))
                repositoryInterfaceName = configuredInterfaceName;

            repositoryInterfaceNamespace = repositoryAttribute.NamedArguments
                .FirstOrDefault(argument => argument.Key == "Namespace").Value.Value as string;
        }

        string? softDeleteColumn = null;
        var softDeleteAttr = classSymbol.GetAttributes().FirstOrDefault(a =>
            a.AttributeClass?.Name == "SoftDeleteAttribute" ||
            a.AttributeClass?.ToDisplayString() == "LinKit.Data.SoftDeleteAttribute");
        if (softDeleteAttr != null)
        {
            softDeleteColumn = softDeleteAttr.ConstructorArguments.Length > 0 && softDeleteAttr.ConstructorArguments[0].Value is string sCol
                ? sCol
                : "DeletedAt";
        }

        string? tenantColumn = null;
        var tenantAttr = classSymbol.GetAttributes().FirstOrDefault(a =>
            a.AttributeClass?.Name == "MultiTenantAttribute" ||
            a.AttributeClass?.ToDisplayString() == "LinKit.Data.MultiTenantAttribute");
        if (tenantAttr != null)
        {
            tenantColumn = tenantAttr.ConstructorArguments.Length > 0 && tenantAttr.ConstructorArguments[0].Value is string tCol
                ? tCol
                : "TenantId";
        }

        var properties = new List<PropertyModel>();
        // Traverse class hierarchy to include inherited properties (walk up base types)
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var currentSymbol = classSymbol as INamedTypeSymbol;
        var columnOrders = new Dictionary<string, int>(StringComparer.Ordinal);
        while (currentSymbol != null && currentSymbol.SpecialType == SpecialType.None)
        {
            var levelStart = properties.Count;
            foreach (var member in currentSymbol.GetMembers().OfType<IPropertySymbol>())
            {
                if (member.IsStatic || member.DeclaredAccessibility != Accessibility.Public) continue;
                if (member.GetMethod == null || member.SetMethod == null) continue;
                if (!seen.Add(member.Name)) continue; // derived class takes precedence

            // Check [IgnoreColumn]
            if (member.GetAttributes().Any(a => a.AttributeClass?.Name == "IgnoreColumnAttribute" || a.AttributeClass?.ToDisplayString() == "LinKit.Data.IgnoreColumnAttribute"))
                continue;

            string colName = member.Name;
            string? customSqlType = null;
            int? maxLength = null;
            bool isFixedLength = false;
            int? precision = null;
            int? scale = null;
            string? defaultValueSql = null;
            bool isUnicode = true;
            bool isJson = false;
            bool isConcurrencyCheck = false;
            string? colComment = null;
            string? fkTargetTable = null;
            string? fkTargetColumn = null;
            int? fkOnDelete = null;

            var colAttr = member.GetAttributes().FirstOrDefault(a =>
                a.AttributeClass?.Name == "ColumnAttribute" || a.AttributeClass?.ToDisplayString() == "LinKit.Data.ColumnAttribute");
            if (colAttr != null)
            {
                if (colAttr.ConstructorArguments.Length > 0 && colAttr.ConstructorArguments[0].Value is string cName)
                    colName = cName;

                var orderNamed = colAttr.NamedArguments.FirstOrDefault(kv => kv.Key == "Order");
                if (orderNamed.Value.Value is int orderVal) columnOrders[member.Name] = orderVal;

                var customSqlNamed = colAttr.NamedArguments.FirstOrDefault(kv => kv.Key == "CustomSqlType");
                if (customSqlNamed.Value.Value is string cSql) customSqlType = cSql;

                var lenNamed = colAttr.NamedArguments.FirstOrDefault(kv => kv.Key == "Length");
                if (lenNamed.Value.Value is int lenVal && lenVal > 0) maxLength = lenVal;

                var precNamed = colAttr.NamedArguments.FirstOrDefault(kv => kv.Key == "Precision");
                if (precNamed.Value.Value is int pVal && pVal > 0) precision = pVal;

                var scaleNamed = colAttr.NamedArguments.FirstOrDefault(kv => kv.Key == "Scale");
                if (scaleNamed.Value.Value is int sScaleVal && sScaleVal > 0) scale = sScaleVal;

                var commNamed = colAttr.NamedArguments.FirstOrDefault(kv => kv.Key == "Comment");
                if (commNamed.Value.Value is string cmVal) colComment = cmVal;
            }

            // [MaxLength]
            var maxLenAttr = member.GetAttributes().FirstOrDefault(a =>
                a.AttributeClass?.Name == "MaxLengthAttribute" || a.AttributeClass?.ToDisplayString() == "LinKit.Data.MaxLengthAttribute");
            if (maxLenAttr != null && maxLenAttr.ConstructorArguments.Length > 0 && maxLenAttr.ConstructorArguments[0].Value is int mlVal)
            {
                maxLength = mlVal;
            }

            // [FixedLength]
            var fixedLenAttr = member.GetAttributes().FirstOrDefault(a =>
                a.AttributeClass?.Name == "FixedLengthAttribute" || a.AttributeClass?.ToDisplayString() == "LinKit.Data.FixedLengthAttribute");
            if (fixedLenAttr != null && fixedLenAttr.ConstructorArguments.Length > 0 && fixedLenAttr.ConstructorArguments[0].Value is int flVal)
            {
                maxLength = flVal;
                isFixedLength = true;
            }

            // [DecimalPrecision]
            var decPrecAttr = member.GetAttributes().FirstOrDefault(a =>
                a.AttributeClass?.Name == "DecimalPrecisionAttribute" || a.AttributeClass?.ToDisplayString() == "LinKit.Data.DecimalPrecisionAttribute");
            if (decPrecAttr != null && decPrecAttr.ConstructorArguments.Length > 0)
            {
                if (decPrecAttr.ConstructorArguments[0].Value is int pVal) precision = pVal;
                if (decPrecAttr.ConstructorArguments.Length > 1 && decPrecAttr.ConstructorArguments[1].Value is int scVal) scale = scVal;
            }

            // [DefaultValueSql]
            var defValAttr = member.GetAttributes().FirstOrDefault(a =>
                a.AttributeClass?.Name == "DefaultValueSqlAttribute" || a.AttributeClass?.ToDisplayString() == "LinKit.Data.DefaultValueSqlAttribute");
            if (defValAttr != null && defValAttr.ConstructorArguments.Length > 0 && defValAttr.ConstructorArguments[0].Value is string defVal)
            {
                defaultValueSql = defVal;
            }

            // [Unicode]
            var uniAttr = member.GetAttributes().FirstOrDefault(a =>
                a.AttributeClass?.Name == "UnicodeAttribute" || a.AttributeClass?.ToDisplayString() == "LinKit.Data.UnicodeAttribute");
            if (uniAttr != null && uniAttr.ConstructorArguments.Length > 0 && uniAttr.ConstructorArguments[0].Value is bool uniVal)
            {
                isUnicode = uniVal;
            }

            // [JsonColumn]
            string? jsonContextTypeFullName = null;
            var jsonAttr = member.GetAttributes().FirstOrDefault(a =>
                a.AttributeClass?.Name == "JsonColumnAttribute" || a.AttributeClass?.ToDisplayString() == "LinKit.Data.JsonColumnAttribute");
            if (jsonAttr != null)
            {
                isJson = true;
                if (jsonAttr.ConstructorArguments.Length > 0 && jsonAttr.ConstructorArguments[0].Value is ITypeSymbol customCtx)
                {
                    jsonContextTypeFullName = customCtx.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                }
                else
                {
                    var namedCtx = jsonAttr.NamedArguments.FirstOrDefault(k => k.Key == "JsonSerializerContextType");
                    if (namedCtx.Value.Value is ITypeSymbol namedCustomCtx)
                    {
                        jsonContextTypeFullName = namedCustomCtx.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                    }
                }
            }

            // [ConcurrencyCheck]
            if (member.GetAttributes().Any(a => a.AttributeClass?.Name == "ConcurrencyCheckAttribute" || a.AttributeClass?.ToDisplayString() == "LinKit.Data.ConcurrencyCheckAttribute"))
            {
                isConcurrencyCheck = true;
            }

            // [Comment]
            var commAttr = member.GetAttributes().FirstOrDefault(a =>
                a.AttributeClass?.Name == "CommentAttribute" || a.AttributeClass?.ToDisplayString() == "LinKit.Data.CommentAttribute");
            if (commAttr != null && commAttr.ConstructorArguments.Length > 0 && commAttr.ConstructorArguments[0].Value is string commStr)
            {
                colComment = commStr;
            }

            // [ForeignKey]
            var fkAttr = member.GetAttributes().FirstOrDefault(a =>
                a.AttributeClass?.Name == "ForeignKeyAttribute" || a.AttributeClass?.ToDisplayString() == "LinKit.Data.ForeignKeyAttribute");
            if (fkAttr != null && fkAttr.ConstructorArguments.Length > 0 && fkAttr.ConstructorArguments[0].Value is string targetTable)
            {
                fkTargetTable = targetTable;
                var targetColArg = fkAttr.NamedArguments.FirstOrDefault(k => k.Key == "TargetColumn");
                if (targetColArg.Value.Value is string tCol) fkTargetColumn = tCol;

                var onDeleteArg = fkAttr.NamedArguments.FirstOrDefault(k => k.Key == "OnDelete");
                if (onDeleteArg.Value.Value is int odVal) fkOnDelete = odVal;
            }

            // Check if member is Enum
            bool isEnum = member.Type.TypeKind == TypeKind.Enum ||
                         (member.Type is INamedTypeSymbol ntsEnum && ntsEnum.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T && ntsEnum.TypeArguments[0].TypeKind == TypeKind.Enum);

            bool isPk = false;
            bool isAutoIncrement = false;
            var pkAttr = member.GetAttributes().FirstOrDefault(a =>
                a.AttributeClass?.Name == "PrimaryKeyAttribute" || a.AttributeClass?.ToDisplayString() == "LinKit.Data.PrimaryKeyAttribute");
            if (pkAttr != null)
            {
                isPk = true;
                if (pkAttr.ConstructorArguments.Length > 0 && pkAttr.ConstructorArguments[0].Value is bool auto)
                {
                    isAutoIncrement = auto;
                }
                var autoNamed = pkAttr.NamedArguments.FirstOrDefault(kv => kv.Key == "AutoIncrement");
                if (autoNamed.Value.Value is bool autoNamedVal) isAutoIncrement = autoNamedVal;
            }
            else if (member.Name.Equals("Id", StringComparison.OrdinalIgnoreCase) ||
                     member.Name.Equals(classSymbol.Name + "Id", StringComparison.OrdinalIgnoreCase))
            {
                isPk = true;
                if (member.Type.SpecialType == SpecialType.System_Int32 || member.Type.SpecialType == SpecialType.System_Int64)
                {
                    isAutoIncrement = true;
                }
            }

            // Identity only makes sense for integral keys; Guid/string/etc. keys are assigned by the application.
            if (isAutoIncrement && member.Type.SpecialType != SpecialType.System_Int32 && member.Type.SpecialType != SpecialType.System_Int64 && member.Type.SpecialType != SpecialType.System_Int16)
                isAutoIncrement = false;

            bool isIndexed = false;
            bool isUniqueIndex = false;
            string? idxName = null;
            var idxAttr = member.GetAttributes().FirstOrDefault(a =>
                a.AttributeClass?.Name == "IndexedAttribute" || a.AttributeClass?.ToDisplayString() == "LinKit.Data.IndexedAttribute");
            if (idxAttr != null)
            {
                isIndexed = true;
                if (idxAttr.ConstructorArguments.Length > 0 && idxAttr.ConstructorArguments[0].Value is bool uVal)
                {
                    isUniqueIndex = uVal;
                }
                var uNamed = idxAttr.NamedArguments.FirstOrDefault(kv => kv.Key == "IsUnique");
                if (uNamed.Value.Value is bool uNamedVal) isUniqueIndex = uNamedVal;

                var inNamed = idxAttr.NamedArguments.FirstOrDefault(kv => kv.Key == "IndexName");
                if (inNamed.Value.Value is string inVal) idxName = inVal;
            }

            bool isNullable = member.NullableAnnotation == NullableAnnotation.Annotated ||
                             member.Type.IsReferenceType ||
                             (member.Type is INamedTypeSymbol nts && nts.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T);

            var underlyingType = (member.Type is INamedTypeSymbol ntsNullable && ntsNullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
                ? ntsNullable.TypeArguments[0]
                : member.Type;

                properties.Add(new PropertyModel
                {
                    PropertyName = member.Name,
                    ColumnName = colName,
                    TypeFullName = member.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    CleanTypeName = member.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    UnderlyingClrTypeName = underlyingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", ""),
                    IsPrimaryKey = isPk,
                    IsAutoIncrement = isAutoIncrement,
                    IsNullable = isNullable,
                    IsIndexed = isIndexed,
                    IsUniqueIndex = isUniqueIndex,
                    IndexName = idxName,
                    CustomSqlType = customSqlType,
                    MaxLength = maxLength,
                    IsFixedLength = isFixedLength,
                    Precision = precision,
                    Scale = scale,
                    DefaultValueSql = defaultValueSql,
                    IsUnicode = isUnicode,
                    IsJson = isJson,
                    IsConcurrencyCheck = isConcurrencyCheck,
                    Comment = colComment,
                    ForeignKeyTargetTable = fkTargetTable,
                    ForeignKeyTargetColumn = fkTargetColumn,
                    ForeignKeyOnDelete = fkOnDelete,
                    IsEnum = isEnum,
                    JsonContextTypeFullName = jsonContextTypeFullName
                });
            }
            // Base-class members go before derived ones.
            if (levelStart > 0 && properties.Count > levelStart)
            {
                var block = properties.GetRange(levelStart, properties.Count - levelStart);
                properties.RemoveRange(levelStart, block.Count);
                properties.InsertRange(0, block);
            }
            currentSymbol = currentSymbol.BaseType;
        }

        if (columnOrders.Count > 0)
        {
            // Stable sort: LINQ OrderBy preserves relative order of equal keys.
            var sorted = properties.OrderBy(p => columnOrders.TryGetValue(p.PropertyName, out var o) ? o : 0).ToList();
            properties.Clear();
            properties.AddRange(sorted);
        }

        bool hasRecognizedPrimaryKey = properties.Any(p => p.IsPrimaryKey);
        var pk = properties.FirstOrDefault(p => p.IsPrimaryKey) ?? properties.FirstOrDefault();

        return new EntityModel
        {
            Name = classSymbol.Name,
            Namespace = classSymbol.ContainingNamespace.IsGlobalNamespace ? "" : classSymbol.ContainingNamespace.ToDisplayString(),
            FullName = classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            AssemblyName = classSymbol.ContainingAssembly.Name,
            RepositoryInterfaceName = repositoryInterfaceName,
            RepositoryInterfaceNamespace = repositoryInterfaceNamespace,
            TableName = tableName,
            Schema = schema,
            TableComment = tableComment,
            CheckConstraints = checkConstraints,
            CompositeIndexes = compositeIndexes,
            SoftDeleteColumn = softDeleteColumn,
            TenantColumn = tenantColumn,
            Location = classSymbol.Locations.FirstOrDefault(),
            HasFallbackPrimaryKey = !hasRecognizedPrimaryKey,
            Properties = properties,
            PrimaryKey = pk
        };
    }

    private static RepositoryInterfaceModel? GetRepositorySemanticModel(GeneratorSyntaxContext ctx)
    {
        var interfaceDecl = (InterfaceDeclarationSyntax)ctx.Node;
        var symbol = ctx.SemanticModel.GetDeclaredSymbol(interfaceDecl);
        if (symbol is not INamedTypeSymbol interfaceSymbol) return null;

        var repoAttr = interfaceSymbol.GetAttributes().FirstOrDefault(a =>
            a.AttributeClass?.Name == "DataRepositoryAttribute" ||
            a.AttributeClass?.ToDisplayString() == "LinKit.Data.DataRepositoryAttribute");

        if (repoAttr == null && !interfaceSymbol.Name.EndsWith("Repository", StringComparison.Ordinal))
            return null;

        string? entityFullName = null;
        string? idTypeFullName = null;

        // Check IRepository<TEntity, TId> interface inheritance
        foreach (var iface in interfaceSymbol.AllInterfaces)
        {
            if (iface.Name == "IRepository" && iface.TypeArguments.Length == 2)
            {
                entityFullName = iface.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                idTypeFullName = iface.TypeArguments[1].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                break;
            }
        }

        var methods = new List<MethodQueryModel>();
        foreach (var member in interfaceSymbol.GetMembers().OfType<IMethodSymbol>())
        {
            var sqlAttr = member.GetAttributes().FirstOrDefault(a =>
                a.AttributeClass?.Name == "SqlQueryAttribute" || a.AttributeClass?.ToDisplayString() == "LinKit.Data.SqlQueryAttribute");

            string? sql = null;
            if (sqlAttr != null && sqlAttr.ConstructorArguments.Length > 0 && sqlAttr.ConstructorArguments[0].Value is string sVal)
            {
                sql = sVal;
            }

            bool returnsList = member.ReturnType.ToDisplayString().Contains("List<") ||
                               member.ReturnType.ToDisplayString().Contains("IEnumerable<");

            var parameters = member.Parameters.Select(p => new ParameterModel
            {
                Name = p.Name,
                TypeFullName = p.Type.ToDisplayString(FullyQualifiedNullableFormat)
            }).ToList();

            methods.Add(new MethodQueryModel
            {
                MethodName = member.Name,
                ReturnTypeFullName = member.ReturnType.ToDisplayString(FullyQualifiedNullableFormat),
                ReturnsList = returnsList,
                CustomSql = sql,
                Parameters = parameters
            });
        }

        return new RepositoryInterfaceModel
        {
            InterfaceName = interfaceSymbol.Name,
            InterfaceFullName = interfaceSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            EntityFullName = entityFullName ?? "",
            IdTypeFullName = idTypeFullName ?? "global::System.Guid",
            QueryMethods = methods
        };
    }

    private static string GenerateMappers(List<EntityModel> entities, string generatedNamespace)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Data;");
        sb.AppendLine("using System.Data.Common;");
        sb.AppendLine("using System.Text.Json;");
        sb.AppendLine("using LinKit.Data;");
        sb.AppendLine();
        sb.AppendLine($"namespace {generatedNamespace};");
        sb.AppendLine();
        sb.AppendLine("public static class DataReaderGeneratedExtensions");
        sb.AppendLine("{");
        sb.AppendLine("    private static int ResolveOrdinal(DbDataReader reader, string column)");
        sb.AppendLine("    {");
        sb.AppendLine("        try { return reader.GetOrdinal(column); }");
        sb.AppendLine("        catch (IndexOutOfRangeException) { return -1; }");
        sb.AppendLine("    }");
        sb.AppendLine();
        foreach (var entity in entities)
        {
            // We build a ColumnOrdinalMap once per reader call using GetOrdinal(), which is O(1) per column
            // after the initial mapping. This is safe for any SQL that returns these columns.
            sb.AppendLine($"    public static int[] {entity.Name}Ordinals(this DbDataReader reader)");
            sb.AppendLine("    {");
            sb.AppendLine($"        var ords = new int[{entity.Properties.Count}];");
            for (int i = 0; i < entity.Properties.Count; i++)
            {
                sb.AppendLine($"        ords[{i}] = ResolveOrdinal(reader, \"{entity.Properties[i].ColumnName}\");");
            }
            sb.AppendLine("        return ords;");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine($"    public static {entity.FullName} Read{entity.Name}(this DbDataReader reader) => reader.Read{entity.Name}(reader.{entity.Name}Ordinals());");
            sb.AppendLine();
            sb.AppendLine($"    public static {entity.FullName} Read{entity.Name}(this DbDataReader reader, int[] ords)");
            sb.AppendLine("    {");
            sb.AppendLine($"        var entity = new {entity.FullName}();");

            for (int propIndex = 0; propIndex < entity.Properties.Count; propIndex++)
            {
                var p = entity.Properties[propIndex];
                var cleanType = p.CleanTypeName.TrimEnd('?');

                // Ordinals are resolved by column NAME once per result set; columns absent from the SELECT are skipped.
                sb.AppendLine($"        {{");
                sb.AppendLine($"            int ord_{p.PropertyName} = ords[{propIndex}];");
                sb.AppendLine($"            if (ord_{p.PropertyName} >= 0 && !reader.IsDBNull(ord_{p.PropertyName}))");
                sb.Append($"            ");

                if (p.IsJson)
                {
                    if (!string.IsNullOrEmpty(p.JsonContextTypeFullName))
                    {
                        sb.AppendLine($"{{ var s = reader.GetString(ord_{p.PropertyName}); entity.{p.PropertyName} = !string.IsNullOrEmpty(s) ? ({p.TypeFullName})JsonSerializer.Deserialize(s, typeof({p.TypeFullName}), {p.JsonContextTypeFullName}.Default)! : default!; }}");
                    }
                    else
                    {
                        sb.AppendLine($"{{ var s = reader.GetString(ord_{p.PropertyName}); var ctx = LinKitDataJsonSerializerAccessor.Context; entity.{p.PropertyName} = !string.IsNullOrEmpty(s) ? (ctx != null ? ({p.TypeFullName})JsonSerializer.Deserialize(s, typeof({p.TypeFullName}), ctx)! : JsonSerializer.Deserialize<{p.TypeFullName}>(s)!) : default!; }}");
                    }
                }
                else if (p.IsEnum)
                {
                    sb.AppendLine($"{{ var val = reader.GetValue(ord_{p.PropertyName}); entity.{p.PropertyName} = val is string s ? ({p.TypeFullName})Enum.Parse(typeof({p.TypeFullName}), s, true) : ({p.TypeFullName})Convert.ToInt32(val); }}");
                }
                else if (cleanType is "string" or "String")
                {
                    sb.AppendLine($"{{ var val = reader.GetValue(ord_{p.PropertyName}); entity.{p.PropertyName} = val?.ToString() ?? string.Empty; }}");
                }
                else if (cleanType is "int" or "Int32")
                {
                    sb.AppendLine($"{{ var val = reader.GetValue(ord_{p.PropertyName}); entity.{p.PropertyName} = val is int iv ? iv : Convert.ToInt32(val); }}");
                }
                else if (cleanType is "long" or "Int64")
                {
                    sb.AppendLine($"{{ var val = reader.GetValue(ord_{p.PropertyName}); entity.{p.PropertyName} = val is long lv ? lv : Convert.ToInt64(val); }}");
                }
                else if (cleanType is "short" or "Int16")
                {
                    sb.AppendLine($"{{ var val = reader.GetValue(ord_{p.PropertyName}); entity.{p.PropertyName} = val is short sv ? sv : Convert.ToInt16(val); }}");
                }
                else if (cleanType is "byte" or "Byte")
                {
                    sb.AppendLine($"{{ var val = reader.GetValue(ord_{p.PropertyName}); entity.{p.PropertyName} = val is byte bv ? bv : Convert.ToByte(val); }}");
                }
                else if (cleanType is "sbyte" or "SByte")
                {
                    sb.AppendLine($"{{ var val = reader.GetValue(ord_{p.PropertyName}); entity.{p.PropertyName} = val is sbyte sbv ? sbv : Convert.ToSByte(val); }}");
                }
                else if (cleanType is "uint" or "UInt32")
                {
                    sb.AppendLine($"{{ var val = reader.GetValue(ord_{p.PropertyName}); entity.{p.PropertyName} = val is uint uiv ? uiv : Convert.ToUInt32(val); }}");
                }
                else if (cleanType is "ulong" or "UInt64")
                {
                    sb.AppendLine($"{{ var val = reader.GetValue(ord_{p.PropertyName}); entity.{p.PropertyName} = val is ulong ulv ? ulv : Convert.ToUInt64(val); }}");
                }
                else if (cleanType is "ushort" or "UInt16")
                {
                    sb.AppendLine($"{{ var val = reader.GetValue(ord_{p.PropertyName}); entity.{p.PropertyName} = val is ushort usv ? usv : Convert.ToUInt16(val); }}");
                }
                else if (cleanType is "bool" or "Boolean")
                {
                    sb.AppendLine($"{{ var val = reader.GetValue(ord_{p.PropertyName}); entity.{p.PropertyName} = val is bool b ? b : (bool.TryParse(val.ToString(), out var pb) ? pb : (Convert.ToInt64(val) != 0)); }}");
                }
                else if (cleanType is "char" or "Char")
                {
                    sb.AppendLine($"{{ var val = reader.GetValue(ord_{p.PropertyName}); entity.{p.PropertyName} = val is char ch ? ch : (val.ToString()!.Length > 0 ? val.ToString()![0] : '\0'); }}");
                }
                else if (cleanType is "decimal" or "Decimal")
                {
                    sb.AppendLine($"{{ var val = reader.GetValue(ord_{p.PropertyName}); entity.{p.PropertyName} = val is decimal dec ? dec : Convert.ToDecimal(val, System.Globalization.CultureInfo.InvariantCulture); }}");
                }
                else if (cleanType is "double" or "Double")
                {
                    sb.AppendLine($"{{ var val = reader.GetValue(ord_{p.PropertyName}); entity.{p.PropertyName} = val is double dv ? dv : Convert.ToDouble(val, System.Globalization.CultureInfo.InvariantCulture); }}");
                }
                else if (cleanType is "float" or "Single")
                {
                    sb.AppendLine($"{{ var val = reader.GetValue(ord_{p.PropertyName}); entity.{p.PropertyName} = val is float fv ? fv : Convert.ToSingle(val, System.Globalization.CultureInfo.InvariantCulture); }}");
                }
                else if (cleanType is "Guid")
                {
                    sb.AppendLine($"{{ var val = reader.GetValue(ord_{p.PropertyName}); entity.{p.PropertyName} = val is Guid g ? g : (val is byte[] bytes && bytes.Length == 16 ? new Guid(bytes) : Guid.Parse(val.ToString()!)); }}");
                }
                else if (cleanType is "DateTime")
                {
                    sb.AppendLine($"{{ var val = reader.GetValue(ord_{p.PropertyName}); entity.{p.PropertyName} = val is DateTime dt ? dt : (val is long ticks ? new DateTime(ticks, DateTimeKind.Utc) : DateTime.Parse(val.ToString()!, System.Globalization.CultureInfo.InvariantCulture)); }}");
                }
                else if (cleanType is "DateTimeOffset")
                {
                    sb.AppendLine($"{{ var val = reader.GetValue(ord_{p.PropertyName}); entity.{p.PropertyName} = val is DateTimeOffset dto ? dto : (val is DateTime dt ? new DateTimeOffset(dt) : DateTimeOffset.Parse(val.ToString()!, System.Globalization.CultureInfo.InvariantCulture)); }}");
                }
                else if (cleanType is "DateOnly")
                {
                    sb.AppendLine($"{{ var val = reader.GetValue(ord_{p.PropertyName}); entity.{p.PropertyName} = val is DateOnly d ? d : (val is DateTime dt ? DateOnly.FromDateTime(dt) : DateOnly.Parse(val.ToString()!, System.Globalization.CultureInfo.InvariantCulture)); }}");
                }
                else if (cleanType is "TimeOnly")
                {
                    sb.AppendLine($"{{ var val = reader.GetValue(ord_{p.PropertyName}); entity.{p.PropertyName} = val is TimeOnly t ? t : (val is TimeSpan ts ? TimeOnly.FromTimeSpan(ts) : TimeOnly.Parse(val.ToString()!, System.Globalization.CultureInfo.InvariantCulture)); }}");
                }
                else if (cleanType is "TimeSpan")
                {
                    sb.AppendLine($"{{ var val = reader.GetValue(ord_{p.PropertyName}); entity.{p.PropertyName} = val is TimeSpan ts ? ts : (val is long ticks ? TimeSpan.FromTicks(ticks) : TimeSpan.Parse(val.ToString()!, System.Globalization.CultureInfo.InvariantCulture)); }}");
                }
                else if (cleanType is "byte[]" or "Byte[]")
                {
                    sb.AppendLine($"{{ var val = reader.GetValue(ord_{p.PropertyName}); entity.{p.PropertyName} = val is byte[] bytes ? bytes : (val is string s ? Convert.FromBase64String(s) : (byte[])val); }}");
                }
                else
                {
                    sb.AppendLine($"{{ entity.{p.PropertyName} = ({p.TypeFullName})reader.GetValue(ord_{p.PropertyName}); }}");
                }
                sb.AppendLine($"        }}");
            }

            sb.AppendLine("        return entity;");
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string GenerateSchemaProvider(List<EntityModel> entities, string generatedNamespace)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using LinKit.Data;");
        sb.AppendLine();
        sb.AppendLine($"namespace {generatedNamespace};");
        sb.AppendLine();
        sb.AppendLine("public sealed class GeneratedTableSchemaProvider : ITableSchemaProvider");
        sb.AppendLine("{");
        sb.AppendLine("    private static readonly TableSchemaMetadata[] _tables = new TableSchemaMetadata[]");
        sb.AppendLine("    {");

        foreach (var entity in entities)
        {
            var schemaStr = entity.Schema != null ? $"\"{entity.Schema}\"" : "null";
            var commentStr = entity.TableComment != null ? $"\"{entity.TableComment.Replace("\"", "\\\"")}\"" : "null";

            sb.AppendLine("        new TableSchemaMetadata");
            sb.AppendLine("        {");
            sb.AppendLine($"            TableName = \"{entity.TableName}\",");
            sb.AppendLine($"            Schema = {schemaStr},");
            sb.AppendLine($"            TableComment = {commentStr},");
            sb.AppendLine($"            EntityType = typeof({entity.FullName}),");
            sb.AppendLine("            CheckConstraints = new CheckConstraintMetadata[]");
            sb.AppendLine("            {");
            foreach (var chk in entity.CheckConstraints)
            {
                var chkName = chk.Name != null ? $"\"{chk.Name}\"" : "null";
                sb.AppendLine($"                new CheckConstraintMetadata {{ SqlExpression = \"{chk.SqlExpression.Replace("\"", "\\\"")}\", Name = {chkName} }},");
            }
            sb.AppendLine("            },");
            sb.AppendLine("            Columns = new ColumnSchemaMetadata[]");
            sb.AppendLine("            {");

            foreach (var p in entity.Properties)
            {
                var customSql = p.CustomSqlType != null ? $"\"{p.CustomSqlType}\"" : "null";
                var idxName = p.IndexName != null ? $"\"{p.IndexName}\"" : "null";
                var maxLen = p.MaxLength.HasValue ? p.MaxLength.Value.ToString() : "null";
                var prec = p.Precision.HasValue ? p.Precision.Value.ToString() : "null";
                var scale = p.Scale.HasValue ? p.Scale.Value.ToString() : "null";
                var defVal = p.DefaultValueSql != null ? $"\"{p.DefaultValueSql.Replace("\"", "\\\"")}\"" : "null";
                var comm = p.Comment != null ? $"\"{p.Comment.Replace("\"", "\\\"")}\"" : "null";
                var fkTable = p.ForeignKeyTargetTable != null ? $"\"{p.ForeignKeyTargetTable}\"" : "null";
                var fkCol = p.ForeignKeyTargetColumn != null ? $"\"{p.ForeignKeyTargetColumn}\"" : "null";
                var fkOnDel = p.ForeignKeyOnDelete.HasValue ? $"(OnDeleteAction){p.ForeignKeyOnDelete.Value}" : "null";

                sb.AppendLine("                new ColumnSchemaMetadata");
                sb.AppendLine("                {");
                sb.AppendLine($"                    PropertyName = \"{p.PropertyName}\",");
                sb.AppendLine($"                    ColumnName = \"{p.ColumnName}\",");
                sb.AppendLine($"                    ClrTypeName = \"{p.UnderlyingClrTypeName}\",");
                sb.AppendLine($"                    IsPrimaryKey = {(p.IsPrimaryKey ? "true" : "false")},");
                sb.AppendLine($"                    IsAutoIncrement = {(p.IsAutoIncrement ? "true" : "false")},");
                sb.AppendLine($"                    IsNullable = {(p.IsNullable ? "true" : "false")},");
                sb.AppendLine($"                    IsIndexed = {(p.IsIndexed ? "true" : "false")},");
                sb.AppendLine($"                    IsUniqueIndex = {(p.IsUniqueIndex ? "true" : "false")},");
                sb.AppendLine($"                    IndexName = {idxName},");
                sb.AppendLine($"                    CustomSqlType = {customSql},");
                sb.AppendLine($"                    MaxLength = {maxLen},");
                sb.AppendLine($"                    IsFixedLength = {(p.IsFixedLength ? "true" : "false")},");
                sb.AppendLine($"                    Precision = {prec},");
                sb.AppendLine($"                    Scale = {scale},");
                sb.AppendLine($"                    DefaultValueSql = {defVal},");
                sb.AppendLine($"                    IsUnicode = {(p.IsUnicode ? "true" : "false")},");
                sb.AppendLine($"                    IsJson = {(p.IsJson ? "true" : "false")},");
                sb.AppendLine($"                    IsEnum = {(p.IsEnum ? "true" : "false")},");
                sb.AppendLine($"                    IsConcurrencyCheck = {(p.IsConcurrencyCheck ? "true" : "false")},");
                sb.AppendLine($"                    Comment = {comm},");
                sb.AppendLine($"                    ForeignKeyTargetTable = {fkTable},");
                sb.AppendLine($"                    ForeignKeyTargetColumn = {fkCol},");
                sb.AppendLine($"                    ForeignKeyOnDelete = {fkOnDel}");
                sb.AppendLine("                },");
            }

            sb.AppendLine("            },");
            sb.AppendLine("            Indexes = new IndexSchemaMetadata[]");
            sb.AppendLine("            {");
            foreach (var cIdx in entity.CompositeIndexes)
            {
                var colList = string.Join(", ", cIdx.ColumnNames.Select(c => $"\"{c}\""));
                sb.AppendLine($"                new IndexSchemaMetadata {{ IndexName = \"{cIdx.IndexName}\", ColumnNames = new string[] {{ {colList} }}, IsUnique = {(cIdx.IsUnique ? "true" : "false")} }},");
            }
            sb.AppendLine("            }");
            sb.AppendLine("        },");
        }

        sb.AppendLine("    };");
        sb.AppendLine();
        sb.AppendLine("    public IReadOnlyList<TableSchemaMetadata> GetTables() => _tables;");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string GenerateRepositories(
        List<EntityModel> entities,
        List<RepositoryInterfaceModel> repos,
        string repositoryNamespace)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Data;");
        sb.AppendLine("using System.Data.Common;");
        sb.AppendLine("using System.Linq.Expressions;");
        sb.AppendLine("using System.Reflection;");
        sb.AppendLine("using System.Threading;");
        sb.AppendLine("using System.Threading.Tasks;");
        sb.AppendLine("using LinKit.Data;");
        sb.AppendLine("using LinKit.Data.Linq;");
        sb.AppendLine("using System.Text.Json;");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(repositoryNamespace))
        {
            sb.AppendLine($"namespace {repositoryNamespace};");
        }
        sb.AppendLine();

        foreach (var entity in entities)
        {
            var pk = entity.PrimaryKey;
            var pkType = pk?.TypeFullName ?? "global::System.Guid";
            var pkColName = pk?.ColumnName ?? "id";
            var pkPropName = pk?.PropertyName ?? "Id";

            // Find matching custom interface if declared
            var customInterface = repos.FirstOrDefault(r =>
                r.EntityFullName == entity.FullName ||
                r.InterfaceFullName == entity.RepositoryInterfaceFullName);
            var implementedInterfaces = new List<string>();
            if (entity.RepositoryInterfaceFullName is not null)
                implementedInterfaces.Add(entity.RepositoryInterfaceFullName);
            if (customInterface is not null &&
                !implementedInterfaces.Contains(customInterface.InterfaceFullName, StringComparer.Ordinal))
                implementedInterfaces.Add(customInterface.InterfaceFullName);
            implementedInterfaces.Add($"IRepository<{entity.FullName}, {pkType}>");
            var interfaceInheritance = $": {string.Join(", ", implementedInterfaces)}";

            // ANSI (PostgreSQL, SQLite)
            var allColsAnsi = string.Join(", ", entity.Properties.Select(p => $"\\\"{p.ColumnName}\\\""));
            var insertCols = entity.Properties.Where(p => !p.IsAutoIncrement).ToList();
            var autoPk = pk != null && pk.IsAutoIncrement && (pk.TypeFullName == "int" || pk.TypeFullName == "long");
            var insertColNamesAnsi = string.Join(", ", insertCols.Select(p => $"\\\"{p.ColumnName}\\\""));
            var insertParamNames = string.Join(", ", insertCols.Select(p => $"@{p.ColumnName}"));
            var updateCols = entity.Properties.Where(p => !p.IsPrimaryKey).ToList();
            var updateSetsAnsi = string.Join(", ", updateCols.Select(p => $"\\\"{p.ColumnName}\\\" = @{p.ColumnName}"));

            // SQL Server (T-SQL)
            var allColsSql = string.Join(", ", entity.Properties.Select(p => $"[{p.ColumnName}]"));
            var insertColNamesSql = string.Join(", ", insertCols.Select(p => $"[{p.ColumnName}]"));
            var updateSetsSql = string.Join(", ", updateCols.Select(p => $"[{p.ColumnName}] = @{p.ColumnName}"));

            // MySQL
            var allColsMySql = string.Join(", ", entity.Properties.Select(p => $"`{p.ColumnName}`"));
            var insertColNamesMySql = string.Join(", ", insertCols.Select(p => $"`{p.ColumnName}`"));
            var updateSetsMySql = string.Join(", ", updateCols.Select(p => $"`{p.ColumnName}` = @{p.ColumnName}"));

            // Oracle
            var insertParamNamesOracle = string.Join(", ", insertCols.Select(p => $":{p.ColumnName}"));
            var updateSetsOracle = string.Join(", ", updateCols.Select(p => $"\\\"{p.ColumnName}\\\" = :{p.ColumnName}"));

            sb.AppendLine($"public partial class {entity.Name}Repository {interfaceInheritance}");
            sb.AppendLine("{");
            sb.AppendLine("    private readonly IDbConnectionFactory _factory;");
            sb.AppendLine("    private readonly IUnitOfWork _uow;");
            sb.AppendLine("    internal readonly DatabaseDialect _dialect;");
            sb.AppendLine("    private readonly string _selectByIdSql;");
            sb.AppendLine("    private readonly string _selectListSql;");
            sb.AppendLine("    private readonly string _pagedCountSql;");
            sb.AppendLine("    private readonly string _pagedItemsSql;");
            sb.AppendLine("    private readonly string _insertSql;");
            sb.AppendLine("    private readonly string _updateSql;");
            sb.AppendLine("    private readonly string _deleteSql;");
            sb.AppendLine("    private readonly string _countSql;");
            sb.AppendLine("    private readonly string _existsSql;");
            sb.AppendLine();
            sb.AppendLine("    private static readonly Dictionary<string, string> _propertyToColumn = new(StringComparer.OrdinalIgnoreCase)");
            sb.AppendLine("    {");
            foreach (var prop in entity.Properties)
            {
                sb.AppendLine($"        {{ \"{prop.PropertyName}\", \"{prop.ColumnName}\" }},");
            }
            sb.AppendLine("    };");
            sb.AppendLine();
            sb.AppendLine($"    public {entity.Name}Repository(IDbConnectionFactory factory, IUnitOfWork uow)");
            sb.AppendLine("    {");
            sb.AppendLine("        _factory = factory;");
            sb.AppendLine("        _uow = uow;");
            sb.AppendLine("        _dialect = factory.Dialect;");
            bool hasSoftDelete = !string.IsNullOrEmpty(entity.SoftDeleteColumn);
            string softCol = entity.SoftDeleteColumn ?? "DeletedAt";

            sb.AppendLine("        switch (_dialect)");
            sb.AppendLine("        {");
            sb.AppendLine("            case DatabaseDialect.SqlServer:");
            if (hasSoftDelete)
            {
                sb.AppendLine($"                _selectByIdSql = \"SELECT TOP 1 {allColsSql} FROM [{entity.TableName}] WHERE [{pkColName}] = @id AND [{softCol}] IS NULL;\";");
                sb.AppendLine($"                _selectListSql = \"SELECT {allColsSql} FROM [{entity.TableName}] WHERE [{softCol}] IS NULL ORDER BY [{pkColName}] ASC OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY;\";");
                sb.AppendLine($"                _pagedCountSql = \"SELECT COUNT(*) FROM [{entity.TableName}] WHERE [{softCol}] IS NULL;\";");
                sb.AppendLine($"                _pagedItemsSql = \"SELECT {allColsSql} FROM [{entity.TableName}] WHERE [{softCol}] IS NULL ORDER BY [{pkColName}] ASC OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY;\";");
                sb.AppendLine($"                _deleteSql = \"UPDATE [{entity.TableName}] SET [{softCol}] = SYSUTCDATETIME() WHERE [{pkColName}] = @id;\";");
                sb.AppendLine($"                _countSql = \"SELECT COUNT(*) FROM [{entity.TableName}] WHERE [{softCol}] IS NULL;\";");
                sb.AppendLine($"                _existsSql = \"SELECT TOP 1 1 FROM [{entity.TableName}] WHERE [{pkColName}] = @id AND [{softCol}] IS NULL;\";");
            }
            else
            {
                sb.AppendLine($"                _selectByIdSql = \"SELECT TOP 1 {allColsSql} FROM [{entity.TableName}] WHERE [{pkColName}] = @id;\";");
                sb.AppendLine($"                _selectListSql = \"SELECT {allColsSql} FROM [{entity.TableName}] ORDER BY [{pkColName}] ASC OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY;\";");
                sb.AppendLine($"                _pagedCountSql = \"SELECT COUNT(*) FROM [{entity.TableName}];\";");
                sb.AppendLine($"                _pagedItemsSql = \"SELECT {allColsSql} FROM [{entity.TableName}] ORDER BY [{pkColName}] ASC OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY;\";");
                sb.AppendLine($"                _deleteSql = \"DELETE FROM [{entity.TableName}] WHERE [{pkColName}] = @id;\";");
                sb.AppendLine($"                _countSql = \"SELECT COUNT(*) FROM [{entity.TableName}];\";");
                sb.AppendLine($"                _existsSql = \"SELECT TOP 1 1 FROM [{entity.TableName}] WHERE [{pkColName}] = @id;\";");
            }
            sb.AppendLine($"                _insertSql = \"INSERT INTO [{entity.TableName}] ({insertColNamesSql}){(autoPk ? " OUTPUT INSERTED.[" + pkColName + "]" : "")} VALUES ({insertParamNames});\";");
            sb.AppendLine($"                _updateSql = \"UPDATE [{entity.TableName}] SET {updateSetsSql} WHERE [{pkColName}] = @id;\";");
            sb.AppendLine("                break;");
            sb.AppendLine("            case DatabaseDialect.MySql:");
            if (hasSoftDelete)
            {
                sb.AppendLine($"                _selectByIdSql = \"SELECT {allColsMySql} FROM `{entity.TableName}` WHERE `{pkColName}` = @id AND `{softCol}` IS NULL LIMIT 1;\";");
                sb.AppendLine($"                _selectListSql = \"SELECT {allColsMySql} FROM `{entity.TableName}` WHERE `{softCol}` IS NULL ORDER BY `{pkColName}` ASC LIMIT @take OFFSET @skip;\";");
                sb.AppendLine($"                _pagedCountSql = \"SELECT COUNT(*) FROM `{entity.TableName}` WHERE `{softCol}` IS NULL;\";");
                sb.AppendLine($"                _pagedItemsSql = \"SELECT {allColsMySql} FROM `{entity.TableName}` WHERE `{softCol}` IS NULL ORDER BY `{pkColName}` ASC LIMIT @take OFFSET @skip;\";");
                sb.AppendLine($"                _deleteSql = \"UPDATE `{entity.TableName}` SET `{softCol}` = CURRENT_TIMESTAMP WHERE `{pkColName}` = @id;\";");
                sb.AppendLine($"                _countSql = \"SELECT COUNT(*) FROM `{entity.TableName}` WHERE `{softCol}` IS NULL;\";");
                sb.AppendLine($"                _existsSql = \"SELECT 1 FROM `{entity.TableName}` WHERE `{pkColName}` = @id AND `{softCol}` IS NULL LIMIT 1;\";");
            }
            else
            {
                sb.AppendLine($"                _selectByIdSql = \"SELECT {allColsMySql} FROM `{entity.TableName}` WHERE `{pkColName}` = @id LIMIT 1;\";");
                sb.AppendLine($"                _selectListSql = \"SELECT {allColsMySql} FROM `{entity.TableName}` ORDER BY `{pkColName}` ASC LIMIT @take OFFSET @skip;\";");
                sb.AppendLine($"                _pagedCountSql = \"SELECT COUNT(*) FROM `{entity.TableName}`;\";");
                sb.AppendLine($"                _pagedItemsSql = \"SELECT {allColsMySql} FROM `{entity.TableName}` ORDER BY `{pkColName}` ASC LIMIT @take OFFSET @skip;\";");
                sb.AppendLine($"                _deleteSql = \"DELETE FROM `{entity.TableName}` WHERE `{pkColName}` = @id;\";");
                sb.AppendLine($"                _countSql = \"SELECT COUNT(*) FROM `{entity.TableName}`;\";");
                sb.AppendLine($"                _existsSql = \"SELECT 1 FROM `{entity.TableName}` WHERE `{pkColName}` = @id LIMIT 1;\";");
            }
            sb.AppendLine($"                _insertSql = \"INSERT INTO `{entity.TableName}` ({insertColNamesMySql}) VALUES ({insertParamNames});{(autoPk ? " SELECT LAST_INSERT_ID();" : "")}\";");
            sb.AppendLine($"                _updateSql = \"UPDATE `{entity.TableName}` SET {updateSetsMySql} WHERE `{pkColName}` = @id;\";");
            sb.AppendLine("                break;");
            sb.AppendLine("            case DatabaseDialect.Oracle:");
            if (hasSoftDelete)
            {
                sb.AppendLine($"                _selectByIdSql = \"SELECT {allColsAnsi} FROM \\\"{entity.TableName}\\\" WHERE \\\"{pkColName}\\\" = :id AND \\\"{softCol}\\\" IS NULL FETCH FIRST 1 ROWS ONLY;\";");
                sb.AppendLine($"                _selectListSql = \"SELECT {allColsAnsi} FROM \\\"{entity.TableName}\\\" WHERE \\\"{softCol}\\\" IS NULL ORDER BY \\\"{pkColName}\\\" ASC OFFSET :skip ROWS FETCH NEXT :take ROWS ONLY;\";");
                sb.AppendLine($"                _pagedCountSql = \"SELECT COUNT(*) FROM \\\"{entity.TableName}\\\" WHERE \\\"{softCol}\\\" IS NULL;\";");
                sb.AppendLine($"                _pagedItemsSql = \"SELECT {allColsAnsi} FROM \\\"{entity.TableName}\\\" WHERE \\\"{softCol}\\\" IS NULL ORDER BY \\\"{pkColName}\\\" ASC OFFSET :skip ROWS FETCH NEXT :take ROWS ONLY;\";");
                sb.AppendLine($"                _deleteSql = \"UPDATE \\\"{entity.TableName}\\\" SET \\\"{softCol}\\\" = CURRENT_TIMESTAMP WHERE \\\"{pkColName}\\\" = :id;\";");
                sb.AppendLine($"                _countSql = \"SELECT COUNT(*) FROM \\\"{entity.TableName}\\\" WHERE \\\"{softCol}\\\" IS NULL;\";");
                sb.AppendLine($"                _existsSql = \"SELECT 1 FROM \\\"{entity.TableName}\\\" WHERE \\\"{pkColName}\\\" = :id AND \\\"{softCol}\\\" IS NULL FETCH FIRST 1 ROWS ONLY;\";");
            }
            else
            {
                sb.AppendLine($"                _selectByIdSql = \"SELECT {allColsAnsi} FROM \\\"{entity.TableName}\\\" WHERE \\\"{pkColName}\\\" = :id FETCH FIRST 1 ROWS ONLY;\";");
                sb.AppendLine($"                _selectListSql = \"SELECT {allColsAnsi} FROM \\\"{entity.TableName}\\\" ORDER BY \\\"{pkColName}\\\" ASC OFFSET :skip ROWS FETCH NEXT :take ROWS ONLY;\";");
                sb.AppendLine($"                _pagedCountSql = \"SELECT COUNT(*) FROM \\\"{entity.TableName}\\\";\";");
                sb.AppendLine($"                _pagedItemsSql = \"SELECT {allColsAnsi} FROM \\\"{entity.TableName}\\\" ORDER BY \\\"{pkColName}\\\" ASC OFFSET :skip ROWS FETCH NEXT :take ROWS ONLY;\";");
                sb.AppendLine($"                _deleteSql = \"DELETE FROM \\\"{entity.TableName}\\\" WHERE \\\"{pkColName}\\\" = :id;\";");
                sb.AppendLine($"                _countSql = \"SELECT COUNT(*) FROM \\\"{entity.TableName}\\\";\";");
                sb.AppendLine($"                _existsSql = \"SELECT 1 FROM \\\"{entity.TableName}\\\" WHERE \\\"{pkColName}\\\" = :id FETCH FIRST 1 ROWS ONLY;\";");
            }
            sb.AppendLine($"                _insertSql = \"INSERT INTO \\\"{entity.TableName}\\\" ({insertColNamesAnsi}) VALUES ({insertParamNamesOracle}){(autoPk ? " RETURNING \\\"" + pkColName + "\\\" INTO :lkid" : "")}\";");
            sb.AppendLine($"                _updateSql = \"UPDATE \\\"{entity.TableName}\\\" SET {updateSetsOracle} WHERE \\\"{pkColName}\\\" = :id;\";");
            sb.AppendLine("                break;");
            sb.AppendLine("            default: // Sqlite, PostgreSql (ANSI standard)");
            if (hasSoftDelete)
            {
                sb.AppendLine($"                _selectByIdSql = \"SELECT {allColsAnsi} FROM \\\"{entity.TableName}\\\" WHERE \\\"{pkColName}\\\" = @id AND \\\"{softCol}\\\" IS NULL LIMIT 1;\";");
                sb.AppendLine($"                _selectListSql = \"SELECT {allColsAnsi} FROM \\\"{entity.TableName}\\\" WHERE \\\"{softCol}\\\" IS NULL ORDER BY \\\"{pkColName}\\\" ASC LIMIT @take OFFSET @skip;\";");
                sb.AppendLine($"                _pagedCountSql = \"SELECT COUNT(*) FROM \\\"{entity.TableName}\\\" WHERE \\\"{softCol}\\\" IS NULL;\";");
                sb.AppendLine($"                _pagedItemsSql = \"SELECT {allColsAnsi} FROM \\\"{entity.TableName}\\\" ORDER BY \\\"{pkColName}\\\" ASC LIMIT @take OFFSET @skip;\";");
                sb.AppendLine($"                _deleteSql = \"UPDATE \\\"{entity.TableName}\\\" SET \\\"{softCol}\\\" = CURRENT_TIMESTAMP WHERE \\\"{pkColName}\\\" = @id;\";");
                sb.AppendLine($"                _countSql = \"SELECT COUNT(*) FROM \\\"{entity.TableName}\\\" WHERE \\\"{softCol}\\\" IS NULL;\";");
                sb.AppendLine($"                _existsSql = \"SELECT 1 FROM \\\"{entity.TableName}\\\" WHERE \\\"{pkColName}\\\" = @id AND \\\"{softCol}\\\" IS NULL LIMIT 1;\";");
            }
            else
            {
                sb.AppendLine($"                _selectByIdSql = \"SELECT {allColsAnsi} FROM \\\"{entity.TableName}\\\" WHERE \\\"{pkColName}\\\" = @id LIMIT 1;\";");
                sb.AppendLine($"                _selectListSql = \"SELECT {allColsAnsi} FROM \\\"{entity.TableName}\\\" ORDER BY \\\"{pkColName}\\\" ASC LIMIT @take OFFSET @skip;\";");
                sb.AppendLine($"                _pagedCountSql = \"SELECT COUNT(*) FROM \\\"{entity.TableName}\\\";\";");
                sb.AppendLine($"                _pagedItemsSql = \"SELECT {allColsAnsi} FROM \\\"{entity.TableName}\\\" ORDER BY \\\"{pkColName}\\\" ASC LIMIT @take OFFSET @skip;\";");
                sb.AppendLine($"                _deleteSql = \"DELETE FROM \\\"{entity.TableName}\\\" WHERE \\\"{pkColName}\\\" = @id;\";");
                sb.AppendLine($"                _countSql = \"SELECT COUNT(*) FROM \\\"{entity.TableName}\\\";\";");
                sb.AppendLine($"                _existsSql = \"SELECT 1 FROM \\\"{entity.TableName}\\\" WHERE \\\"{pkColName}\\\" = @id LIMIT 1;\";");
            }
            sb.AppendLine($"                _insertSql = \"INSERT INTO \\\"{entity.TableName}\\\" ({insertColNamesAnsi}) VALUES ({insertParamNames}){(autoPk ? " RETURNING \\\"" + pkColName + "\\\"" : "")};\";");
            sb.AppendLine($"                _updateSql = \"UPDATE \\\"{entity.TableName}\\\" SET {updateSetsAnsi} WHERE \\\"{pkColName}\\\" = @id;\";");
            sb.AppendLine("                break;");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    private string ParamName(string name) => _dialect == DatabaseDialect.Oracle ? \":\" + name : \"@\" + name;");
            sb.AppendLine("    // Oracle stores Guid as VARCHAR2(36) and its driver cannot bind Guid parameters.");
            sb.AppendLine("    private object DbValue(object? value) => value is null ? DBNull.Value : (value is Guid guid && _dialect == DatabaseDialect.Oracle ? guid.ToString() : value);");
            sb.AppendLine();

            // Helper to get connection and transaction
            sb.AppendLine("    private async ValueTask<(DbConnection Connection, DbTransaction? Transaction, bool ShouldDispose)> GetConnectionAsync(CancellationToken ct)");
            sb.AppendLine("    {");
            sb.AppendLine("        if (_uow.CurrentTransaction != null)");
            sb.AppendLine("        {");
            sb.AppendLine("            return (_uow.CurrentTransaction.Connection!, _uow.CurrentTransaction, false);");
            sb.AppendLine("        }");
            sb.AppendLine("        var conn = await _factory.CreateConnectionAsync(ct);");
            sb.AppendLine("        if (conn.State != ConnectionState.Open) await conn.OpenAsync(ct);");
            sb.AppendLine("        return (conn, null, true);");
            sb.AppendLine("    }");
            sb.AppendLine();

            // Helper to attach dynamic parameters
            sb.AppendLine("    internal void AttachParameters(DbCommand cmd, object? parameters)");
            sb.AppendLine("    {");
            sb.AppendLine("        if (parameters == null) return;");
            sb.AppendLine("        if (parameters is IEnumerable<DbParameter> dbParams)");
            sb.AppendLine("        {");
            sb.AppendLine("            foreach (var p in dbParams) cmd.Parameters.Add(p);");
            sb.AppendLine("            return;");
            sb.AppendLine("        }");
            sb.AppendLine("        var prefix = _dialect == DatabaseDialect.Oracle ? \":\" : \"@\";");
            sb.AppendLine("        if (parameters is IEnumerable<KeyValuePair<string, object?>> kvpParams)");
            sb.AppendLine("        {");
            sb.AppendLine("            foreach (var kvp in kvpParams)");
            sb.AppendLine("            {");
            sb.AppendLine("                var p = cmd.CreateParameter();");
            sb.AppendLine("                var key = kvp.Key.TrimStart('@', ':');");
            sb.AppendLine("                p.ParameterName = prefix + key;");
            sb.AppendLine("                p.Value = DbValue(kvp.Value);");
            sb.AppendLine("                cmd.Parameters.Add(p);");
            sb.AppendLine("            }");
            sb.AppendLine("            return;");
            sb.AppendLine("        }");
            sb.AppendLine("        if (parameters is IEnumerable<(string Name, object? Value)> tupleParams)");
            sb.AppendLine("        {");
            sb.AppendLine("            foreach (var item in tupleParams)");
            sb.AppendLine("            {");
            sb.AppendLine("                var p = cmd.CreateParameter();");
            sb.AppendLine("                var key = item.Name.TrimStart('@', ':');");
            sb.AppendLine("                p.ParameterName = prefix + key;");
            sb.AppendLine("                p.Value = DbValue(item.Value);");
            sb.AppendLine("                cmd.Parameters.Add(p);");
            sb.AppendLine("            }");
            sb.AppendLine("            return;");
            sb.AppendLine("        }");
            sb.AppendLine("        // Native AOT: reflection on anonymous/POCO types is NOT supported.");
            sb.AppendLine("        // Use IEnumerable<(string Name, object? Value)>, Dictionary<string, object?>, or IEnumerable<DbParameter>.");
            sb.AppendLine("        throw new NotSupportedException(");
            sb.AppendLine("            $\"AttachParameters: parameter type '{parameters?.GetType().Name}' is not supported in AOT mode. \"");
            sb.AppendLine("            + \"Use IEnumerable<(string Name, object? Value)>, Dictionary<string, object?>, or IEnumerable<DbParameter>.\");");
            sb.AppendLine("    }");
            sb.AppendLine();

            sb.AppendLine($"    public async Task<{entity.FullName}?> GetByIdAsync({pkType} id, CancellationToken ct = default)");
            sb.AppendLine("    {");
            sb.AppendLine("        var (conn, tx, shouldDispose) = await GetConnectionAsync(ct);");
            sb.AppendLine("        try");
            sb.AppendLine("        {");
            sb.AppendLine("            await using var cmd = conn.CreateCommand();");
            sb.AppendLine("            cmd.Transaction = tx;");
            sb.AppendLine("            cmd.CommandText = _selectByIdSql;");
            sb.AppendLine("            var p = cmd.CreateParameter();");
            sb.AppendLine("            p.ParameterName = ParamName(\"id\");");
            sb.AppendLine("            p.Value = DbValue(id);");
            sb.AppendLine("            cmd.Parameters.Add(p);");
            sb.AppendLine();
            sb.AppendLine("            await using var reader = await cmd.ExecuteReaderAsync(ct);");
            sb.AppendLine($"            return await reader.ReadAsync(ct) ? reader.Read{entity.Name}() : null;");
            sb.AppendLine("        }");
            sb.AppendLine("        finally");
            sb.AppendLine("        {");
            sb.AppendLine("            if (shouldDispose) await conn.DisposeAsync();");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine();

            sb.AppendLine($"    public async Task<List<{entity.FullName}>> ListAsync(int skip = 0, int take = 100, CancellationToken ct = default)");
            sb.AppendLine("    {");
            sb.AppendLine("        var (conn, tx, shouldDispose) = await GetConnectionAsync(ct);");
            sb.AppendLine("        try");
            sb.AppendLine("        {");
            sb.AppendLine("            await using var cmd = conn.CreateCommand();");
            sb.AppendLine("            cmd.Transaction = tx;");
            sb.AppendLine("            cmd.CommandText = _selectListSql;");
            sb.AppendLine("            var pTake = cmd.CreateParameter(); pTake.ParameterName = ParamName(\"take\"); pTake.Value = take; cmd.Parameters.Add(pTake);");
            sb.AppendLine("            var pSkip = cmd.CreateParameter(); pSkip.ParameterName = ParamName(\"skip\"); pSkip.Value = skip; cmd.Parameters.Add(pSkip);");
            sb.AppendLine();
            sb.AppendLine($"            var list = new List<{entity.FullName}>();");
            sb.AppendLine("            await using var reader = await cmd.ExecuteReaderAsync(ct);");
            sb.AppendLine($"            var ords = reader.{entity.Name}Ordinals();");
            sb.AppendLine("            while (await reader.ReadAsync(ct))");
            sb.AppendLine("            {");
            sb.AppendLine($"                list.Add(reader.Read{entity.Name}(ords));");
            sb.AppendLine("            }");
            sb.AppendLine("            return list;");
            sb.AppendLine("        }");
            sb.AppendLine("        finally");
            sb.AppendLine("        {");
            sb.AppendLine("            if (shouldDispose) await conn.DisposeAsync();");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine();

            sb.AppendLine($"    public async Task<PagedResult<{entity.FullName}>> GetPagedAsync(int pageNumber, int pageSize, CancellationToken ct = default)");
            sb.AppendLine("    {");
            sb.AppendLine("        if (pageNumber < 1) pageNumber = 1;");
            sb.AppendLine("        if (pageSize < 1) pageSize = 10;");
            sb.AppendLine("        int skip = (pageNumber - 1) * pageSize;");
            sb.AppendLine("        var (conn, tx, shouldDispose) = await GetConnectionAsync(ct);");
            sb.AppendLine("        try");
            sb.AppendLine("        {");
            sb.AppendLine("            await using var countCmd = conn.CreateCommand();");
            sb.AppendLine("            countCmd.Transaction = tx;");
            sb.AppendLine("            countCmd.CommandText = _pagedCountSql;");
            sb.AppendLine("            var totalObj = await countCmd.ExecuteScalarAsync(ct);");
            sb.AppendLine("            long totalCount = Convert.ToInt64(totalObj);");
            sb.AppendLine();
            sb.AppendLine("            await using var cmd = conn.CreateCommand();");
            sb.AppendLine("            cmd.Transaction = tx;");
            sb.AppendLine("            cmd.CommandText = _pagedItemsSql;");
            sb.AppendLine("            var pTake = cmd.CreateParameter(); pTake.ParameterName = ParamName(\"take\"); pTake.Value = pageSize; cmd.Parameters.Add(pTake);");
            sb.AppendLine("            var pSkip = cmd.CreateParameter(); pSkip.ParameterName = ParamName(\"skip\"); pSkip.Value = skip; cmd.Parameters.Add(pSkip);");
            sb.AppendLine();
            sb.AppendLine($"            var items = new List<{entity.FullName}>();");
            sb.AppendLine("            await using var reader = await cmd.ExecuteReaderAsync(ct);");
            sb.AppendLine($"            var ords = reader.{entity.Name}Ordinals();");
            sb.AppendLine("            while (await reader.ReadAsync(ct))");
            sb.AppendLine("            {");
            sb.AppendLine($"                items.Add(reader.Read{entity.Name}(ords));");
            sb.AppendLine("            }");
            sb.AppendLine();
            sb.AppendLine($"            return new PagedResult<{entity.FullName}>");
            sb.AppendLine("            {");
            sb.AppendLine("                Items = items,");
            sb.AppendLine("                TotalCount = totalCount,");
            sb.AppendLine("                PageNumber = pageNumber,");
            sb.AppendLine("                PageSize = pageSize");
            sb.AppendLine("            };");
            sb.AppendLine("        }");
            sb.AppendLine("        finally");
            sb.AppendLine("        {");
            sb.AppendLine("            if (shouldDispose) await conn.DisposeAsync();");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine();

            sb.AppendLine($"    public async Task InsertAsync({entity.FullName} entity, CancellationToken ct = default)");
            sb.AppendLine("    {");
            sb.AppendLine("        var (conn, tx, shouldDispose) = await GetConnectionAsync(ct);");
            sb.AppendLine("        try");
            sb.AppendLine("        {");
            sb.AppendLine("            await using var cmd = conn.CreateCommand();");
            sb.AppendLine("            cmd.Transaction = tx;");
            sb.AppendLine("            cmd.CommandText = _insertSql;");
            sb.AppendLine();
            foreach (var col in insertCols)
            {
                sb.AppendLine("            {");
                sb.AppendLine("                var p = cmd.CreateParameter();");
                sb.AppendLine($"                p.ParameterName = ParamName(\"{col.ColumnName}\");");
                if (col.IsJson)
                {
                    if (!string.IsNullOrEmpty(col.JsonContextTypeFullName))
                    {
                        sb.AppendLine($"                p.Value = entity.{col.PropertyName} != null ? JsonSerializer.Serialize(entity.{col.PropertyName}, typeof({col.TypeFullName}), {col.JsonContextTypeFullName}.Default) : DBNull.Value;");
                    }
                    else
                    {
                        sb.AppendLine($"                var jsonCtx = LinKitDataJsonSerializerAccessor.Context;");
                        sb.AppendLine($"                p.Value = entity.{col.PropertyName} != null ? (jsonCtx != null ? JsonSerializer.Serialize(entity.{col.PropertyName}, typeof({col.TypeFullName}), jsonCtx) : JsonSerializer.Serialize(entity.{col.PropertyName})) : DBNull.Value;");
                    }
                }
                else if (col.IsEnum)
                {
                    sb.AppendLine($"                p.Value = (int)entity.{col.PropertyName};");
                }
                else
                {
                    sb.AppendLine($"                p.Value = DbValue(entity.{col.PropertyName});");
                }
                sb.AppendLine("                cmd.Parameters.Add(p);");
                sb.AppendLine("            }");
            }
            sb.AppendLine();
            if (autoPk)
            {
                var convert = pk!.TypeFullName == "long" ? "Convert.ToInt64" : "Convert.ToInt32";
                sb.AppendLine("            if (_dialect == DatabaseDialect.Oracle)");
                sb.AppendLine("            {");
                sb.AppendLine("                var idParam = cmd.CreateParameter();");
                sb.AppendLine("                idParam.ParameterName = \":lkid\";");
                sb.AppendLine("                idParam.Direction = ParameterDirection.Output;");
                sb.AppendLine($"                idParam.DbType = {(pk.TypeFullName == "long" ? "DbType.Int64" : "DbType.Int32")};");
                sb.AppendLine("                cmd.Parameters.Add(idParam);");
                sb.AppendLine("                await cmd.ExecuteNonQueryAsync(ct);");
                sb.AppendLine($"                entity.{pkPropName} = {convert}(idParam.Value);");
                sb.AppendLine("            }");
                sb.AppendLine("            else");
                sb.AppendLine("            {");
                sb.AppendLine("                var generatedId = await cmd.ExecuteScalarAsync(ct);");
                sb.AppendLine($"                if (generatedId != null && generatedId != DBNull.Value) entity.{pkPropName} = {convert}(generatedId);");
                sb.AppendLine("            }");
            }
            else
            {
                sb.AppendLine("            await cmd.ExecuteNonQueryAsync(ct);");
            }
            sb.AppendLine("        }");
            sb.AppendLine("        finally");
            sb.AppendLine("        {");
            sb.AppendLine("            if (shouldDispose) await conn.DisposeAsync();");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine();

            // InsertRangeAsync — Batch INSERT with dynamic batch size to respect SQL parameter limits (< 2100)
            int maxInsertBatch = Math.Max(1, Math.Min(500, 2000 / Math.Max(1, insertCols.Count)));
            sb.AppendLine($"    public async Task InsertRangeAsync(IEnumerable<{entity.FullName}> entities, CancellationToken ct = default)");
            sb.AppendLine("    {");
            if (autoPk)
            {
                // Identity keys must be written back to each entity, so rows are inserted one by one.
                sb.AppendLine("        foreach (var item in entities) await InsertAsync(item, ct);");
                sb.AppendLine("    }");
            }
            else
            {
            sb.AppendLine("        if (_dialect == DatabaseDialect.Oracle)");
            sb.AppendLine("        {");
            sb.AppendLine("            foreach (var item in entities) await InsertAsync(item, ct);");
            sb.AppendLine("            return;");
            sb.AppendLine("        }");
            sb.AppendLine($"        var batch = new List<{entity.FullName}>({maxInsertBatch});");
            sb.AppendLine("        foreach (var item in entities)");
            sb.AppendLine("        {");
            sb.AppendLine("            batch.Add(item);");
            sb.AppendLine($"            if (batch.Count >= {maxInsertBatch}) {{ await InsertBatchAsync(batch, ct); batch.Clear(); }}");
            sb.AppendLine("        }");
            sb.AppendLine("        if (batch.Count > 0) await InsertBatchAsync(batch, ct);");
            sb.AppendLine("    }");
            }
            sb.AppendLine();
            sb.AppendLine($"    private async Task InsertBatchAsync(List<{entity.FullName}> items, CancellationToken ct)");
            sb.AppendLine("    {");
            sb.AppendLine("        if (items.Count == 0) return;");
            sb.AppendLine("        var (conn, tx, shouldDispose) = await GetConnectionAsync(ct);");
            sb.AppendLine("        try");
            sb.AppendLine("        {");
            sb.AppendLine("            await using var cmd = conn.CreateCommand();");
            sb.AppendLine("            cmd.Transaction = tx;");
            sb.AppendLine("            var sqlSb = new System.Text.StringBuilder();");

            // Build dialect-aware table + column prefix once
            string batchColsSql    = string.Join(", ", insertCols.Select(p => $"[{p.ColumnName}]"));
            string batchColsAnsi   = string.Join(", ", insertCols.Select(p => $"\\\"{p.ColumnName}\\\""));
            string batchColsMySql  = string.Join(", ", insertCols.Select(p => $"`{p.ColumnName}`"));
            sb.AppendLine("            string tablePrefix = _dialect switch");
            sb.AppendLine("            {");
            sb.AppendLine($"                DatabaseDialect.SqlServer => \"INSERT INTO [{entity.TableName}] ({batchColsSql}) VALUES \",");
            sb.AppendLine($"                DatabaseDialect.MySql => \"INSERT INTO `{entity.TableName}` ({batchColsMySql}) VALUES \",");
            sb.AppendLine($"                _ => \"INSERT INTO \\\"{entity.TableName}\\\" ({batchColsAnsi}) VALUES \"");
            sb.AppendLine("            };");
            sb.AppendLine("            sqlSb.Append(tablePrefix);");
            sb.AppendLine("            for (int row = 0; row < items.Count; row++)");
            sb.AppendLine("            {");
            sb.AppendLine("                if (row > 0) sqlSb.Append(',');");
            string rowParams = string.Join(", ", insertCols.Select(p => "\" + ParamName(\"" + p.ColumnName + "\" + row) + \""));
            sb.AppendLine($"                sqlSb.Append('(');");
            for (int ci = 0; ci < insertCols.Count; ci++)
            {
                var col = insertCols[ci];
                if (ci > 0)
                    sb.AppendLine("                sqlSb.Append(',');");
                sb.AppendLine($"                sqlSb.Append(ParamName(\"{col.ColumnName}\" + row));");
            }
            sb.AppendLine("                sqlSb.Append(')');");
            sb.AppendLine("                var rowItem = items[row];");
            foreach (var col in insertCols)
            {
                sb.AppendLine("                {");
                sb.AppendLine("                    var p = cmd.CreateParameter();");
                sb.AppendLine($"                    p.ParameterName = ParamName(\"{col.ColumnName}\" + row);");
                if (col.IsJson)
                {
                    if (!string.IsNullOrEmpty(col.JsonContextTypeFullName))
                        sb.AppendLine($"                    p.Value = rowItem.{col.PropertyName} != null ? JsonSerializer.Serialize(rowItem.{col.PropertyName}, typeof({col.TypeFullName}), {col.JsonContextTypeFullName}.Default) : (object)DBNull.Value;");
                    else
                        sb.AppendLine($"                    {{ var jctx = LinKitDataJsonSerializerAccessor.Context; p.Value = rowItem.{col.PropertyName} != null ? (jctx != null ? JsonSerializer.Serialize(rowItem.{col.PropertyName}, typeof({col.TypeFullName}), jctx) : JsonSerializer.Serialize(rowItem.{col.PropertyName})) : (object)DBNull.Value; }}");
                }
                else if (col.IsEnum)
                    sb.AppendLine($"                    p.Value = (int)rowItem.{col.PropertyName};");
                else
                    sb.AppendLine($"                    p.Value = DbValue(rowItem.{col.PropertyName});");
                sb.AppendLine("                    cmd.Parameters.Add(p);");
                sb.AppendLine("                }");
            }
            sb.AppendLine("            }");
            sb.AppendLine("            cmd.CommandText = sqlSb.ToString() + \";\";");
            sb.AppendLine("            await cmd.ExecuteNonQueryAsync(ct);");
            sb.AppendLine("        }");
            sb.AppendLine("        finally");
            sb.AppendLine("        {");
            sb.AppendLine("            if (shouldDispose) await conn.DisposeAsync();");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine();

            sb.AppendLine($"    public async Task UpdateAsync({entity.FullName} entity, CancellationToken ct = default)");
            sb.AppendLine("    {");
            sb.AppendLine("        var (conn, tx, shouldDispose) = await GetConnectionAsync(ct);");
            sb.AppendLine("        try");
            sb.AppendLine("        {");
            sb.AppendLine("            await using var cmd = conn.CreateCommand();");
            sb.AppendLine("            cmd.Transaction = tx;");
            sb.AppendLine("            cmd.CommandText = _updateSql;");
            sb.AppendLine();
            foreach (var col in updateCols)
            {
                sb.AppendLine("            {");
                sb.AppendLine("                var p = cmd.CreateParameter();");
                sb.AppendLine($"                p.ParameterName = ParamName(\"{col.ColumnName}\");");
                if (col.IsJson)
                {
                    if (!string.IsNullOrEmpty(col.JsonContextTypeFullName))
                    {
                        sb.AppendLine($"                p.Value = entity.{col.PropertyName} != null ? JsonSerializer.Serialize(entity.{col.PropertyName}, typeof({col.TypeFullName}), {col.JsonContextTypeFullName}.Default) : DBNull.Value;");
                    }
                    else
                    {
                        sb.AppendLine($"                var jsonCtx = LinKitDataJsonSerializerAccessor.Context;");
                        sb.AppendLine($"                p.Value = entity.{col.PropertyName} != null ? (jsonCtx != null ? JsonSerializer.Serialize(entity.{col.PropertyName}, typeof({col.TypeFullName}), jsonCtx) : JsonSerializer.Serialize(entity.{col.PropertyName})) : DBNull.Value;");
                    }
                }
                else if (col.IsEnum)
                {
                    sb.AppendLine($"                p.Value = (int)entity.{col.PropertyName};");
                }
                else
                {
                    sb.AppendLine($"                p.Value = DbValue(entity.{col.PropertyName});");
                }
                sb.AppendLine("                cmd.Parameters.Add(p);");
                sb.AppendLine("            }");
            }
            sb.AppendLine("            {");
            sb.AppendLine("                var p = cmd.CreateParameter();");
            sb.AppendLine("                p.ParameterName = ParamName(\"id\");");
            sb.AppendLine($"                p.Value = DbValue(entity.{pkPropName});");
            sb.AppendLine("                cmd.Parameters.Add(p);");
            sb.AppendLine("            }");
            sb.AppendLine();
            sb.AppendLine("            await cmd.ExecuteNonQueryAsync(ct);");
            sb.AppendLine("        }");
            sb.AppendLine("        finally");
            sb.AppendLine("        {");
            sb.AppendLine("            if (shouldDispose) await conn.DisposeAsync();");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine();

            // Reuses one connection and transaction for the entire batch.
            sb.AppendLine($"    public async Task UpdateRangeAsync(IEnumerable<{entity.FullName}> entities, CancellationToken ct = default)");
            sb.AppendLine("    {");
            sb.AppendLine("        var (conn, tx, shouldDispose) = await GetConnectionAsync(ct);");
            sb.AppendLine("        DbTransaction? localTx = null;");
            sb.AppendLine("        if (tx == null) localTx = await conn.BeginTransactionAsync(ct);");
            sb.AppendLine("        var activeTx = tx ?? localTx;");
            sb.AppendLine("        try");
            sb.AppendLine("        {");
            sb.AppendLine("            foreach (var item in entities)");
            sb.AppendLine("            {");
            sb.AppendLine("                await using var cmd = conn.CreateCommand();");
            sb.AppendLine("                cmd.Transaction = activeTx;");
            sb.AppendLine("                cmd.CommandText = _updateSql;");
            foreach (var col in updateCols)
            {
                sb.AppendLine("                {");
                sb.AppendLine("                    var p = cmd.CreateParameter();");
                sb.AppendLine($"                    p.ParameterName = ParamName(\"{col.ColumnName}\");");
                if (col.IsJson)
                {
                    if (!string.IsNullOrEmpty(col.JsonContextTypeFullName))
                    {
                        sb.AppendLine($"                    p.Value = item.{col.PropertyName} != null ? JsonSerializer.Serialize(item.{col.PropertyName}, typeof({col.TypeFullName}), {col.JsonContextTypeFullName}.Default) : DBNull.Value;");
                    }
                    else
                    {
                        sb.AppendLine($"                    var jsonCtx = LinKitDataJsonSerializerAccessor.Context;");
                        sb.AppendLine($"                    p.Value = item.{col.PropertyName} != null ? (jsonCtx != null ? JsonSerializer.Serialize(item.{col.PropertyName}, typeof({col.TypeFullName}), jsonCtx) : JsonSerializer.Serialize(item.{col.PropertyName})) : DBNull.Value;");
                    }
                }
                else if (col.IsEnum)
                {
                    sb.AppendLine($"                    p.Value = (int)item.{col.PropertyName};");
                }
                else
                {
                    sb.AppendLine($"                    p.Value = DbValue(item.{col.PropertyName});");
                }
                sb.AppendLine("                    cmd.Parameters.Add(p);");
                sb.AppendLine("                }");
            }
            sb.AppendLine("                {");
            sb.AppendLine("                    var p = cmd.CreateParameter();");
            sb.AppendLine("                    p.ParameterName = ParamName(\"id\");");
            sb.AppendLine($"                    p.Value = DbValue(item.{pkPropName});");
            sb.AppendLine("                    cmd.Parameters.Add(p);");
            sb.AppendLine("                }");
            sb.AppendLine("                await cmd.ExecuteNonQueryAsync(ct);");
            sb.AppendLine("            }");
            sb.AppendLine("            if (localTx != null) await localTx.CommitAsync(ct);");
            sb.AppendLine("        }");
            sb.AppendLine("        catch");
            sb.AppendLine("        {");
            sb.AppendLine("            if (localTx != null) await localTx.RollbackAsync(ct);");
            sb.AppendLine("            throw;");
            sb.AppendLine("        }");
            sb.AppendLine("        finally");
            sb.AppendLine("        {");
            sb.AppendLine("            if (localTx != null) await localTx.DisposeAsync();");
            sb.AppendLine("            if (shouldDispose) await conn.DisposeAsync();");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine();

            sb.AppendLine($"    public async Task DeleteAsync({pkType} id, CancellationToken ct = default)");
            sb.AppendLine("    {");
            sb.AppendLine("        var (conn, tx, shouldDispose) = await GetConnectionAsync(ct);");
            sb.AppendLine("        try");
            sb.AppendLine("        {");
            sb.AppendLine("            await using var cmd = conn.CreateCommand();");
            sb.AppendLine("            cmd.Transaction = tx;");
            sb.AppendLine("            cmd.CommandText = _deleteSql;");
            sb.AppendLine("            var p = cmd.CreateParameter();");
            sb.AppendLine("            p.ParameterName = ParamName(\"id\");");
            sb.AppendLine("            p.Value = DbValue(id);");
            sb.AppendLine("            cmd.Parameters.Add(p);");
            sb.AppendLine();
            sb.AppendLine("            await cmd.ExecuteNonQueryAsync(ct);");
            sb.AppendLine("        }");
            sb.AppendLine("        finally");
            sb.AppendLine("        {");
            sb.AppendLine("            if (shouldDispose) await conn.DisposeAsync();");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine();

            // Splits large deletes into chunks to stay within database parameter limits.
            sb.AppendLine($"    public async Task DeleteRangeAsync(IEnumerable<{pkType}> ids, CancellationToken ct = default)");
            sb.AppendLine("    {");
            sb.AppendLine($"        var batch = new List<{pkType}>();");
            sb.AppendLine("        foreach (var id in ids)");
            sb.AppendLine("        {");
            sb.AppendLine("            batch.Add(id);");
            sb.AppendLine("            if (batch.Count >= 500) { await DeleteBatchAsync(batch, ct); batch.Clear(); }");
            sb.AppendLine("        }");
            sb.AppendLine("        if (batch.Count > 0) await DeleteBatchAsync(batch, ct);");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine($"    private async Task DeleteBatchAsync(List<{pkType}> batch, CancellationToken ct)");
            sb.AppendLine("    {");
            sb.AppendLine("        if (batch.Count == 0) return;");
            sb.AppendLine("        var (conn, tx, shouldDispose) = await GetConnectionAsync(ct);");
            sb.AppendLine("        try");
            sb.AppendLine("        {");
            sb.AppendLine("            await using var cmd = conn.CreateCommand();");
            sb.AppendLine("            cmd.Transaction = tx;");
            sb.AppendLine("            var sqlSb = new System.Text.StringBuilder();");
            sb.AppendLine("            string tablePrefix = _dialect switch");
            sb.AppendLine("            {");
            if (hasSoftDelete)
            {
                sb.AppendLine($"                DatabaseDialect.SqlServer => \"UPDATE [{entity.TableName}] SET [{softCol}] = SYSUTCDATETIME() WHERE [{pkColName}] IN (\",");
                sb.AppendLine($"                DatabaseDialect.MySql => \"UPDATE `{entity.TableName}` SET `{softCol}` = CURRENT_TIMESTAMP WHERE `{pkColName}` IN (\",");
                sb.AppendLine($"                _ => \"UPDATE \\\"{entity.TableName}\\\" SET \\\"{softCol}\\\" = CURRENT_TIMESTAMP WHERE \\\"{pkColName}\\\" IN (\"");
            }
            else
            {
                sb.AppendLine($"                DatabaseDialect.SqlServer => \"DELETE FROM [{entity.TableName}] WHERE [{pkColName}] IN (\",");
                sb.AppendLine($"                DatabaseDialect.MySql => \"DELETE FROM `{entity.TableName}` WHERE `{pkColName}` IN (\",");
                sb.AppendLine($"                _ => \"DELETE FROM \\\"{entity.TableName}\\\" WHERE \\\"{pkColName}\\\" IN (\"");
            }
            sb.AppendLine("            };");
            sb.AppendLine("            sqlSb.Append(tablePrefix);");
            sb.AppendLine("            for (int i = 0; i < batch.Count; i++)");
            sb.AppendLine("            {");
            sb.AppendLine("                if (i > 0) sqlSb.Append(',');");
            sb.AppendLine($"                sqlSb.Append(ParamName(\"id\" + i));");
            sb.AppendLine("                var p = cmd.CreateParameter();");
            sb.AppendLine($"                p.ParameterName = ParamName(\"id\" + i);");
            sb.AppendLine("                p.Value = DbValue(batch[i]);");
            sb.AppendLine("                cmd.Parameters.Add(p);");
            sb.AppendLine("            }");
            sb.AppendLine("            sqlSb.Append(\");\");");
            sb.AppendLine("            cmd.CommandText = sqlSb.ToString();");
            sb.AppendLine("            await cmd.ExecuteNonQueryAsync(ct);");
            sb.AppendLine("        }");
            sb.AppendLine("        finally");
            sb.AppendLine("        {");
            sb.AppendLine("            if (shouldDispose) await conn.DisposeAsync();");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine();

            sb.AppendLine("    public async Task<long> CountAsync(CancellationToken ct = default)");
            sb.AppendLine("    {");
            sb.AppendLine("        var (conn, tx, shouldDispose) = await GetConnectionAsync(ct);");
            sb.AppendLine("        try");
            sb.AppendLine("        {");
            sb.AppendLine("            await using var cmd = conn.CreateCommand();");
            sb.AppendLine("            cmd.Transaction = tx;");
            sb.AppendLine("            cmd.CommandText = _countSql;");
            sb.AppendLine("            var res = await cmd.ExecuteScalarAsync(ct);");
            sb.AppendLine("            return Convert.ToInt64(res);");
            sb.AppendLine("        }");
            sb.AppendLine("        finally");
            sb.AppendLine("        {");
            sb.AppendLine("            if (shouldDispose) await conn.DisposeAsync();");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine();

            sb.AppendLine($"    public async Task<bool> ExistsAsync({pkType} id, CancellationToken ct = default)");
            sb.AppendLine("    {");
            sb.AppendLine("        var (conn, tx, shouldDispose) = await GetConnectionAsync(ct);");
            sb.AppendLine("        try");
            sb.AppendLine("        {");
            sb.AppendLine("            await using var cmd = conn.CreateCommand();");
            sb.AppendLine("            cmd.Transaction = tx;");
            sb.AppendLine("            cmd.CommandText = _existsSql;");
            sb.AppendLine("            var p = cmd.CreateParameter();");
            sb.AppendLine("            p.ParameterName = ParamName(\"id\");");
            sb.AppendLine("            p.Value = DbValue(id);");
            sb.AppendLine("            cmd.Parameters.Add(p);");
            sb.AppendLine();
            sb.AppendLine("            var res = await cmd.ExecuteScalarAsync(ct);");
            sb.AppendLine("            return res != null && res != DBNull.Value;");
            sb.AppendLine("        }");
            sb.AppendLine("        finally");
            sb.AppendLine("        {");
            sb.AppendLine("            if (shouldDispose) await conn.DisposeAsync();");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine();

            sb.AppendLine($"    public async Task<List<{entity.FullName}>> QuerySqlAsync(string sql, object? parameters = null, CancellationToken ct = default)");
            sb.AppendLine("    {");
            sb.AppendLine("        var (conn, tx, shouldDispose) = await GetConnectionAsync(ct);");
            sb.AppendLine("        try");
            sb.AppendLine("        {");
            sb.AppendLine("            await using var cmd = conn.CreateCommand();");
            sb.AppendLine("            cmd.Transaction = tx;");
            sb.AppendLine("            cmd.CommandText = sql;");
            sb.AppendLine("            AttachParameters(cmd, parameters);");
            sb.AppendLine();
            sb.AppendLine($"            var list = new List<{entity.FullName}>();");
            sb.AppendLine("            await using var reader = await cmd.ExecuteReaderAsync(ct);");
            sb.AppendLine($"            var ords = reader.{entity.Name}Ordinals();");
            sb.AppendLine("            while (await reader.ReadAsync(ct))");
            sb.AppendLine("            {");
            sb.AppendLine($"                list.Add(reader.Read{entity.Name}(ords));");
            sb.AppendLine("            }");
            sb.AppendLine("            return list;");
            sb.AppendLine("        }");
            sb.AppendLine("        finally");
            sb.AppendLine("        {");
            sb.AppendLine("            if (shouldDispose) await conn.DisposeAsync();");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine();

            sb.AppendLine($"    public async Task<{entity.FullName}?> QuerySingleSqlAsync(string sql, object? parameters = null, CancellationToken ct = default)");
            sb.AppendLine("    {");
            sb.AppendLine("        var (conn, tx, shouldDispose) = await GetConnectionAsync(ct);");
            sb.AppendLine("        try");
            sb.AppendLine("        {");
            sb.AppendLine("            await using var cmd = conn.CreateCommand();");
            sb.AppendLine("            cmd.Transaction = tx;");
            sb.AppendLine("            cmd.CommandText = sql;");
            sb.AppendLine("            AttachParameters(cmd, parameters);");
            sb.AppendLine();
            sb.AppendLine("            await using var reader = await cmd.ExecuteReaderAsync(ct);");
            sb.AppendLine($"            return await reader.ReadAsync(ct) ? reader.Read{entity.Name}() : null;");
            sb.AppendLine("        }");
            sb.AppendLine("        finally");
            sb.AppendLine("        {");
            sb.AppendLine("            if (shouldDispose) await conn.DisposeAsync();");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine();

            sb.AppendLine("    public async Task<int> ExecuteSqlAsync(string sql, object? parameters = null, CancellationToken ct = default)");
            sb.AppendLine("    {");
            sb.AppendLine("        var (conn, tx, shouldDispose) = await GetConnectionAsync(ct);");
            sb.AppendLine("        try");
            sb.AppendLine("        {");
            sb.AppendLine("            await using var cmd = conn.CreateCommand();");
            sb.AppendLine("            cmd.Transaction = tx;");
            sb.AppendLine("            cmd.CommandText = sql;");
            sb.AppendLine("            AttachParameters(cmd, parameters);");
            sb.AppendLine("            return await cmd.ExecuteNonQueryAsync(ct);");
            sb.AppendLine("        }");
            sb.AppendLine("        finally");
            sb.AppendLine("        {");
            sb.AppendLine("            if (shouldDispose) await conn.DisposeAsync();");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine();

            sb.AppendLine("    public async Task<TScalar?> ExecuteScalarSqlAsync<TScalar>(string sql, object? parameters = null, CancellationToken ct = default)");
            sb.AppendLine("    {");
            sb.AppendLine("        var (conn, tx, shouldDispose) = await GetConnectionAsync(ct);");
            sb.AppendLine("        try");
            sb.AppendLine("        {");
            sb.AppendLine("            await using var cmd = conn.CreateCommand();");
            sb.AppendLine("            cmd.Transaction = tx;");
            sb.AppendLine("            cmd.CommandText = sql;");
            sb.AppendLine("            AttachParameters(cmd, parameters);");
            sb.AppendLine("            var res = await cmd.ExecuteScalarAsync(ct);");
            sb.AppendLine("            if (res == null || res == DBNull.Value) return default;");
            sb.AppendLine("            if (res is TScalar scalar) return scalar;");
            sb.AppendLine("            return (TScalar)Convert.ChangeType(res, typeof(TScalar));");
            sb.AppendLine("        }");
            sb.AppendLine("        finally");
            sb.AppendLine("        {");
            sb.AppendLine("            if (shouldDispose) await conn.DisposeAsync();");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine();

            sb.AppendLine($"    public IFluentQuery<{entity.FullName}> Query() => new {entity.Name}FluentQuery(this);");
            sb.AppendLine();
            sb.AppendLine($"    public ILinKitQueryable<{entity.FullName}> AsQueryable() => new LinKitQueryable<{entity.FullName}>(new LinKitQueryProvider<{entity.FullName}>(() => Query(), _dialect, _propertyToColumn));");
            sb.AppendLine($"    public ILinKitQueryable<{entity.FullName}> Table => AsQueryable();");
            sb.AppendLine();

            // Custom methods from interface if any
            if (customInterface != null)
            {
                foreach (var method in customInterface.QueryMethods)
                {
                    if (method.CustomSql is null)
                        continue;

                    if (method.MethodName is "GetByIdAsync" or "ListAsync" or "GetPagedAsync" or "InsertAsync" or "InsertRangeAsync" or "UpdateAsync" or "UpdateRangeAsync" or "DeleteAsync" or "DeleteRangeAsync" or "CountAsync" or "ExistsAsync" or "QuerySqlAsync" or "QuerySingleSqlAsync" or "ExecuteSqlAsync" or "ExecuteScalarSqlAsync" or "Query" or "Table" or "AsQueryable")
                        continue;

                    var paramList = string.Join(", ", method.Parameters.Select(p => $"{p.TypeFullName} {p.Name}"));
                    var cancellationToken = method.Parameters.FirstOrDefault(p =>
                        p.TypeFullName == "global::System.Threading.CancellationToken")?.Name
                        ?? "global::System.Threading.CancellationToken.None";
                    sb.AppendLine();
                    sb.AppendLine($"    public async {method.ReturnTypeFullName} {method.MethodName}({paramList})");
                    sb.AppendLine("    {");
                    sb.AppendLine($"        var (conn, tx, shouldDispose) = await GetConnectionAsync({cancellationToken});");
                    sb.AppendLine("        try");
                    sb.AppendLine("        {");
                    sb.AppendLine("            await using var cmd = conn.CreateCommand();");
                    sb.AppendLine("            cmd.Transaction = tx;");
                    string sqlToRun = method.CustomSql ?? $"SELECT {allColsAnsi} FROM \\\"{entity.TableName}\\\"";
                    sb.AppendLine($"            cmd.CommandText = @\"{sqlToRun}\";");

                    foreach (var p in method.Parameters)
                    {
                        if (p.TypeFullName == "global::System.Threading.CancellationToken") continue;
                        sb.AppendLine("            {");
                        sb.AppendLine("                var p = cmd.CreateParameter();");
                        sb.AppendLine($"                p.ParameterName = ParamName(\"{p.Name}\");");
                        sb.AppendLine($"                p.Value = DbValue({p.Name});");
                        sb.AppendLine("                cmd.Parameters.Add(p);");
                        sb.AppendLine("            }");
                    }

                    if (method.ReturnsList)
                    {
                        sb.AppendLine($"            var list = new List<{entity.FullName}>();");
                        sb.AppendLine($"            await using var reader = await cmd.ExecuteReaderAsync({cancellationToken});");
                        sb.AppendLine($"            var ords = reader.{entity.Name}Ordinals();");
                        sb.AppendLine($"            while (await reader.ReadAsync({cancellationToken}))");
                        sb.AppendLine("            {");
                        sb.AppendLine($"                list.Add(reader.Read{entity.Name}(ords));");
                        sb.AppendLine("            }");
                        sb.AppendLine("            return list;");
                    }
                    else
                    {
                        sb.AppendLine($"            await using var reader = await cmd.ExecuteReaderAsync({cancellationToken});");
                        sb.AppendLine($"            return await reader.ReadAsync({cancellationToken}) ? reader.Read{entity.Name}() : null;");
                    }

                    sb.AppendLine("        }");
                    sb.AppendLine("        finally");
                    sb.AppendLine("        {");
                    sb.AppendLine("            if (shouldDispose) await conn.DisposeAsync();");
                    sb.AppendLine("        }");
                    sb.AppendLine("    }");
                }
            }

            sb.AppendLine($"        public class {entity.Name}FluentQuery : IFluentQuery<{entity.FullName}>");
            sb.AppendLine("    {");
            sb.AppendLine($"        private readonly {entity.Name}Repository _repo;");
            sb.AppendLine("        private readonly List<string> _whereConditions = new List<string>();");
            sb.AppendLine("        private readonly List<object?> _parameters = new List<object?>();");
            sb.AppendLine("        private int _expressionParameterIndex;");
            sb.AppendLine("        private string? _orderBy;");
            sb.AppendLine("        private int _skip = 0;");
            sb.AppendLine("        private int _take = 100;");
            sb.AppendLine();
            sb.AppendLine($"        public {entity.Name}FluentQuery({entity.Name}Repository repo)");
            sb.AppendLine("        {");
            sb.AppendLine("            _repo = repo;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine($"        public IFluentQuery<{entity.FullName}> Where(string sqlCondition, object? parameters = null)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (!string.IsNullOrWhiteSpace(sqlCondition))");
            sb.AppendLine("            {");
            sb.AppendLine("                _whereConditions.Add(sqlCondition);");
            sb.AppendLine("                if (parameters != null) _parameters.Add(parameters);");
            sb.AppendLine("            }");
            sb.AppendLine("            return this;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine($"        public IFluentQuery<{entity.FullName}> Where(Expression<Func<{entity.FullName}, bool>> predicate)");
            sb.AppendLine("        {");
            sb.AppendLine("            ArgumentNullException.ThrowIfNull(predicate);");
            sb.AppendLine("            var translated = new ExpressionSqlTranslator(");
            sb.AppendLine("                _repo._dialect,");
            sb.AppendLine("                _propertyToColumn,");
            sb.AppendLine("                _expressionParameterIndex).TranslatePredicate(predicate);");
            sb.AppendLine("            if (!string.IsNullOrWhiteSpace(translated.WhereSql))");
            sb.AppendLine("            {");
            sb.AppendLine("                _whereConditions.Add(translated.WhereSql);");
            sb.AppendLine("                _parameters.Add(translated.Parameters);");
            sb.AppendLine("                _expressionParameterIndex += translated.Parameters.Count;");
            sb.AppendLine("            }");
            sb.AppendLine("            return this;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        private string QuoteCol(string col)");
            sb.AppendLine("        {");
            sb.AppendLine("            return _repo._dialect switch");
            sb.AppendLine("            {");
            sb.AppendLine("                DatabaseDialect.SqlServer => $\"[{col}]\",");
            sb.AppendLine("                DatabaseDialect.MySql => $\"`{col}`\",");
            sb.AppendLine("                _ => $\"\\\"{col}\\\"\"");
            sb.AppendLine("            };");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine($"        public IFluentQuery<{entity.FullName}> OrderBy(string column)");
            sb.AppendLine("        {");
            sb.AppendLine("            _orderBy = $\"{QuoteCol(column)} ASC\";");
            sb.AppendLine("            return this;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine($"        public IFluentQuery<{entity.FullName}> OrderBy<TKey>(Expression<Func<{entity.FullName}, TKey>> keySelector)");
            sb.AppendLine("        {");
            sb.AppendLine("            _orderBy = new ExpressionSqlTranslator(_repo._dialect, _propertyToColumn).TranslateOrderBy(keySelector);");
            sb.AppendLine("            return this;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine($"        public IFluentQuery<{entity.FullName}> OrderByDescending(string column)");
            sb.AppendLine("        {");
            sb.AppendLine("            _orderBy = $\"{QuoteCol(column)} DESC\";");
            sb.AppendLine("            return this;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine($"        public IFluentQuery<{entity.FullName}> OrderByDescending<TKey>(Expression<Func<{entity.FullName}, TKey>> keySelector)");
            sb.AppendLine("        {");
            sb.AppendLine("            _orderBy = new ExpressionSqlTranslator(_repo._dialect, _propertyToColumn).TranslateOrderBy(keySelector, descending: true);");
            sb.AppendLine("            return this;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine($"        public IFluentQuery<{entity.FullName}> ThenBy<TKey>(Expression<Func<{entity.FullName}, TKey>> keySelector)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (_orderBy is null) throw new InvalidOperationException(\"ThenBy requires an OrderBy expression first.\");");
            sb.AppendLine("            _orderBy += \", \" + new ExpressionSqlTranslator(_repo._dialect, _propertyToColumn).TranslateOrderBy(keySelector);");
            sb.AppendLine("            return this;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine($"        public IFluentQuery<{entity.FullName}> ThenByDescending<TKey>(Expression<Func<{entity.FullName}, TKey>> keySelector)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (_orderBy is null) throw new InvalidOperationException(\"ThenByDescending requires an OrderBy expression first.\");");
            sb.AppendLine("            _orderBy += \", \" + new ExpressionSqlTranslator(_repo._dialect, _propertyToColumn).TranslateOrderBy(keySelector, descending: true);");
            sb.AppendLine("            return this;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine($"        public IFluentQuery<{entity.FullName}> ThenBy(string column)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (_orderBy is null) throw new InvalidOperationException(\"ThenBy requires an OrderBy expression first.\");");
            sb.AppendLine("            _orderBy += \", \" + QuoteCol(column) + \" ASC\";");
            sb.AppendLine("            return this;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine($"        public IFluentQuery<{entity.FullName}> ThenByDescending(string column)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (_orderBy is null) throw new InvalidOperationException(\"ThenByDescending requires an OrderBy expression first.\");");
            sb.AppendLine("            _orderBy += \", \" + QuoteCol(column) + \" DESC\";");
            sb.AppendLine("            return this;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine($"        public IFluentQuery<{entity.FullName}> Skip(int count)");
            sb.AppendLine("        {");
            sb.AppendLine("            _skip = Math.Max(0, count);");
            sb.AppendLine("            return this;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine($"        public IFluentQuery<{entity.FullName}> Take(int count)");
            sb.AppendLine("        {");
            sb.AppendLine("            _take = Math.Max(1, count);");
            sb.AppendLine("            return this;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        private string BuildWhereClause()");
            sb.AppendLine("        {");
            sb.AppendLine("            var conditions = new List<string>(_whereConditions);");
            if (hasSoftDelete)
            {
                sb.AppendLine("            var softDeleteCondition = _repo._dialect switch");
                sb.AppendLine("            {");
                sb.AppendLine($"                DatabaseDialect.SqlServer => \"[{softCol}] IS NULL\",");
                sb.AppendLine($"                DatabaseDialect.MySql => \"`{softCol}` IS NULL\",");
                sb.AppendLine($"                _ => \"\\\"{softCol}\\\" IS NULL\"");
                sb.AppendLine("            };");
                sb.AppendLine("            conditions.Insert(0, softDeleteCondition);");
            }
            sb.AppendLine("            if (conditions.Count == 0) return \"\";");
            sb.AppendLine("            return \" WHERE \" + string.Join(\" AND \", conditions.Select(c => $\"({c})\"));");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine($"        public async Task<List<{entity.FullName}>> ToListAsync(CancellationToken ct = default)");
            sb.AppendLine("        {");
            sb.AppendLine($"            var order = _orderBy ?? $\"{{QuoteCol(\"{pkColName}\")}} ASC\";");
            sb.AppendLine("            string sql = _repo._dialect switch");
            sb.AppendLine("            {");
            sb.AppendLine($"                DatabaseDialect.SqlServer => $\"SELECT {allColsSql} FROM [{entity.TableName}]{{BuildWhereClause()}} ORDER BY {{order}} OFFSET {{_skip}} ROWS FETCH NEXT {{_take}} ROWS ONLY;\",");
            sb.AppendLine($"                DatabaseDialect.MySql => $\"SELECT {allColsMySql} FROM `{entity.TableName}`{{BuildWhereClause()}} ORDER BY {{order}} LIMIT {{_take}} OFFSET {{_skip}};\",");
            sb.AppendLine($"                DatabaseDialect.Oracle => $\"SELECT {allColsAnsi} FROM \\\"{entity.TableName}\\\"{{BuildWhereClause()}} ORDER BY {{order}} OFFSET {{_skip}} ROWS FETCH NEXT {{_take}} ROWS ONLY;\",");
            sb.AppendLine($"                _ => $\"SELECT {allColsAnsi} FROM \\\"{entity.TableName}\\\"{{BuildWhereClause()}} ORDER BY {{order}} LIMIT {{_take}} OFFSET {{_skip}};\"");
            sb.AppendLine("            };");
            sb.AppendLine("            var (conn, tx, shouldDispose) = await _repo.GetConnectionAsync(ct);");
            sb.AppendLine("            try");
            sb.AppendLine("            {");
            sb.AppendLine("                await using var cmd = conn.CreateCommand();");
            sb.AppendLine("                cmd.Transaction = tx;");
            sb.AppendLine("                cmd.CommandText = sql;");
            sb.AppendLine("                foreach (var p in _parameters) _repo.AttachParameters(cmd, p);");
            sb.AppendLine($"                var list = new List<{entity.FullName}>();");
            sb.AppendLine("                await using var reader = await cmd.ExecuteReaderAsync(ct);");
            sb.AppendLine($"                var ords = reader.{entity.Name}Ordinals();");
            sb.AppendLine("                while (await reader.ReadAsync(ct)) list.Add(reader.Read" + entity.Name + "(ords));");
            sb.AppendLine("                return list;");
            sb.AppendLine("            }");
            sb.AppendLine("            finally");
            sb.AppendLine("            {");
            sb.AppendLine("                if (shouldDispose) await conn.DisposeAsync();");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine($"        public async Task<{entity.FullName}?> FirstOrDefaultAsync(CancellationToken ct = default)");
            sb.AppendLine("        {");
            sb.AppendLine($"            var order = _orderBy ?? $\"{{QuoteCol(\"{pkColName}\")}} ASC\";");
            sb.AppendLine("            string sql = _repo._dialect switch");
            sb.AppendLine("            {");
            sb.AppendLine($"                DatabaseDialect.SqlServer => $\"SELECT TOP 1 {allColsSql} FROM [{entity.TableName}]{{BuildWhereClause()}} ORDER BY {{order}};\",");
            sb.AppendLine($"                DatabaseDialect.MySql => $\"SELECT {allColsMySql} FROM `{entity.TableName}`{{BuildWhereClause()}} ORDER BY {{order}} LIMIT 1;\",");
            sb.AppendLine($"                DatabaseDialect.Oracle => $\"SELECT {allColsAnsi} FROM \\\"{entity.TableName}\\\"{{BuildWhereClause()}} ORDER BY {{order}} FETCH FIRST 1 ROWS ONLY;\",");
            sb.AppendLine($"                _ => $\"SELECT {allColsAnsi} FROM \\\"{entity.TableName}\\\"{{BuildWhereClause()}} ORDER BY {{order}} LIMIT 1;\"");
            sb.AppendLine("            };");
            sb.AppendLine("            var (conn, tx, shouldDispose) = await _repo.GetConnectionAsync(ct);");
            sb.AppendLine("            try");
            sb.AppendLine("            {");
            sb.AppendLine("                await using var cmd = conn.CreateCommand();");
            sb.AppendLine("                cmd.Transaction = tx;");
            sb.AppendLine("                cmd.CommandText = sql;");
            sb.AppendLine("                foreach (var p in _parameters) _repo.AttachParameters(cmd, p);");
            sb.AppendLine("                await using var reader = await cmd.ExecuteReaderAsync(ct);");
            sb.AppendLine($"                return await reader.ReadAsync(ct) ? reader.Read{entity.Name}() : null;");
            sb.AppendLine("            }");
            sb.AppendLine("            finally");
            sb.AppendLine("            {");
            sb.AppendLine("                if (shouldDispose) await conn.DisposeAsync();");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        public async Task<long> CountAsync(CancellationToken ct = default)");
            sb.AppendLine("        {");
            sb.AppendLine("            string sql = _repo._dialect switch");
            sb.AppendLine("            {");
            sb.AppendLine($"                DatabaseDialect.SqlServer => $\"SELECT COUNT(*) FROM [{entity.TableName}]{{BuildWhereClause()}};\",");
            sb.AppendLine($"                DatabaseDialect.MySql => $\"SELECT COUNT(*) FROM `{entity.TableName}`{{BuildWhereClause()}};\",");
            sb.AppendLine($"                _ => $\"SELECT COUNT(*) FROM \\\"{entity.TableName}\\\"{{BuildWhereClause()}};\"");
            sb.AppendLine("            };");
            sb.AppendLine("            var (conn, tx, shouldDispose) = await _repo.GetConnectionAsync(ct);");
            sb.AppendLine("            try");
            sb.AppendLine("            {");
            sb.AppendLine("                await using var cmd = conn.CreateCommand();");
            sb.AppendLine("                cmd.Transaction = tx;");
            sb.AppendLine("                cmd.CommandText = sql;");
            sb.AppendLine("                foreach (var p in _parameters) _repo.AttachParameters(cmd, p);");
            sb.AppendLine("                var res = await cmd.ExecuteScalarAsync(ct);");
            sb.AppendLine("                return Convert.ToInt64(res);");
            sb.AppendLine("            }");
            sb.AppendLine("            finally");
            sb.AppendLine("            {");
            sb.AppendLine("                if (shouldDispose) await conn.DisposeAsync();");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        public async Task<bool> AnyAsync(CancellationToken ct = default)");
            sb.AppendLine("        {");
            sb.AppendLine("            string sql = _repo._dialect switch");
            sb.AppendLine("            {");
            sb.AppendLine($"                DatabaseDialect.SqlServer => $\"SELECT TOP 1 1 FROM [{entity.TableName}]{{BuildWhereClause()}};\",");
            sb.AppendLine($"                DatabaseDialect.MySql => $\"SELECT 1 FROM `{entity.TableName}`{{BuildWhereClause()}} LIMIT 1;\",");
            sb.AppendLine($"                DatabaseDialect.Oracle => $\"SELECT 1 FROM \\\"{entity.TableName}\\\"{{BuildWhereClause()}} FETCH FIRST 1 ROWS ONLY;\",");
            sb.AppendLine($"                _ => $\"SELECT 1 FROM \\\"{entity.TableName}\\\"{{BuildWhereClause()}} LIMIT 1;\"");
            sb.AppendLine("            };");
            sb.AppendLine("            var (conn, tx, shouldDispose) = await _repo.GetConnectionAsync(ct);");
            sb.AppendLine("            try");
            sb.AppendLine("            {");
            sb.AppendLine("                await using var cmd = conn.CreateCommand();");
            sb.AppendLine("                cmd.Transaction = tx;");
            sb.AppendLine("                cmd.CommandText = sql;");
            sb.AppendLine("                foreach (var p in _parameters) _repo.AttachParameters(cmd, p);");
            sb.AppendLine("                var res = await cmd.ExecuteScalarAsync(ct);");
            sb.AppendLine("                return res != null && res != DBNull.Value;");
            sb.AppendLine("            }");
            sb.AppendLine("            finally");
            sb.AppendLine("            {");
            sb.AppendLine("                if (shouldDispose) await conn.DisposeAsync();");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine($"        public async Task<PagedResult<{entity.FullName}>> ToPagedListAsync(int pageNumber, int pageSize, CancellationToken ct = default)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (pageNumber < 1) pageNumber = 1;");
            sb.AppendLine("            if (pageSize < 1) pageSize = 10;");
            sb.AppendLine("            _skip = (pageNumber - 1) * pageSize;");
            sb.AppendLine("            _take = pageSize;");
            sb.AppendLine("            long total = await CountAsync(ct);");
            sb.AppendLine("            var items = await ToListAsync(ct);");
            sb.AppendLine($"            return new PagedResult<{entity.FullName}>");
            sb.AppendLine("            {");
            sb.AppendLine("                Items = items,");
            sb.AppendLine("                TotalCount = total,");
            sb.AppendLine("                PageNumber = pageNumber,");
            sb.AppendLine("                PageSize = pageSize");
            sb.AppendLine("            };");
            sb.AppendLine("        }");
            sb.AppendLine("    }");

            sb.AppendLine("}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string GenerateMigrationsRegistry(List<MigrationModel> migrations, string generatedNamespace)
    {
        var sorted = migrations.OrderBy(m => m.Version, StringComparer.Ordinal).ToList();
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using LinKit.Data;");
        sb.AppendLine("using LinKit.Data.Migrations;");
        sb.AppendLine();
        sb.AppendLine($"namespace {generatedNamespace};");
        sb.AppendLine();
        sb.AppendLine("public sealed class GeneratedMigrationRegistry : IMigrationRegistry");
        sb.AppendLine("{");
        sb.AppendLine("    public IReadOnlyList<MigrationDescriptor> GetMigrations() => _migrations;");
        sb.AppendLine();
        sb.AppendLine("    private static readonly MigrationDescriptor[] _migrations = new MigrationDescriptor[]");
        sb.AppendLine("    {");
        foreach (var m in sorted)
        {
            var escapedDesc = (m.Description ?? "").Replace("\"", "\\\"");
            sb.AppendLine($"        new MigrationDescriptor(\"{m.Version}\", \"{escapedDesc}\", typeof({m.ClassFullName}), sp => (IMigration?)sp.GetService(typeof({m.ClassFullName})) ?? new {m.ClassFullName}()),");
        }
        sb.AppendLine("    };");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string GenerateDependencyInjection(
        List<EntityModel> entities,
        List<RepositoryInterfaceModel> repos,
        List<MigrationModel> migrations,
        string rootNamespace,
        string generatedNamespace)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine($"global using {generatedNamespace};");
        sb.AppendLine("using System;");
        sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");
        sb.AppendLine("using LinKit.Data;");
        sb.AppendLine();
        sb.AppendLine($"namespace {generatedNamespace};");
        sb.AppendLine();
        sb.AppendLine("public static class LinKitDataGeneratedServiceExtensions");
        sb.AppendLine("{");
        sb.AppendLine("    public static IServiceCollection AddLinKitData(this IServiceCollection services, Action<LinKitDataOptions> configure)");
        sb.AppendLine("    {");
        sb.AppendLine("        services.AddLinKitDataCore(configure);");
        sb.AppendLine();

        if (entities.Count > 0)
        {
            var schemaProviderType = string.IsNullOrWhiteSpace(rootNamespace)
                ? "GeneratedTableSchemaProvider"
                : $"global::{generatedNamespace}.GeneratedTableSchemaProvider";
            sb.AppendLine($"        services.AddSingleton<ITableSchemaProvider, {schemaProviderType}>();");
            foreach (var entity in entities)
            {
                var pk = entity.PrimaryKey;
                var pkType = pk?.TypeFullName ?? "global::System.Guid";
                var repositoryType = string.IsNullOrWhiteSpace(rootNamespace)
                    ? $"{entity.Name}Repository"
                    : $"global::{rootNamespace}.{entity.Name}Repository";

                // Default IRepository<Entity, TId>
                sb.AppendLine($"        services.AddScoped<IRepository<{entity.FullName}, {pkType}>, {repositoryType}>();");

                if (entity.RepositoryInterfaceFullName is not null)
                {
                    sb.AppendLine($"        services.AddScoped<{entity.RepositoryInterfaceFullName}, {repositoryType}>();");
                }

                var customInterface = repos.FirstOrDefault(r => r.EntityFullName == entity.FullName);
                if (customInterface != null && customInterface.InterfaceFullName != entity.RepositoryInterfaceFullName)
                {
                    sb.AppendLine($"        services.AddScoped<{customInterface.InterfaceFullName}, {repositoryType}>();");
                }
            }
            sb.AppendLine();
        }

        if (migrations.Count > 0)
        {
            var migrationRegistryType = string.IsNullOrWhiteSpace(rootNamespace)
                ? "GeneratedMigrationRegistry"
                : $"global::{generatedNamespace}.GeneratedMigrationRegistry";
            sb.AppendLine($"        services.AddSingleton<LinKit.Data.Migrations.IMigrationRegistry, {migrationRegistryType}>();");
            foreach (var m in migrations)
            {
                sb.AppendLine($"        services.AddTransient<{m.ClassFullName}>();");
            }
            sb.AppendLine();
        }

        sb.AppendLine("        return services;");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string GenerateRepositoryInterfaces(List<EntityModel> entities)
    {
        var repositories = entities.Where(entity => entity.RepositoryInterfaceFullName is not null).ToList();
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();

        foreach (var entity in repositories)
        {
            if (!string.IsNullOrWhiteSpace(entity.RepositoryInterfaceNamespace))
            {
                sb.AppendLine($"namespace {entity.RepositoryInterfaceNamespace}");
                sb.AppendLine("{");
            }

            var indent = string.IsNullOrWhiteSpace(entity.RepositoryInterfaceNamespace) ? "" : "    ";
            var pkType = entity.PrimaryKey?.TypeFullName ?? "global::System.Guid";
            sb.AppendLine($"{indent}public partial interface {entity.RepositoryInterfaceName} : global::LinKit.Data.IRepository<{entity.FullName}, {pkType}>");
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}}}");

            if (!string.IsNullOrWhiteSpace(entity.RepositoryInterfaceNamespace))
                sb.AppendLine("}");

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private class EntityModel
    {
        public string Name { get; set; } = string.Empty;
        public string Namespace { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public string AssemblyName { get; set; } = string.Empty;
        public string? RepositoryInterfaceName { get; set; }
        public string? RepositoryInterfaceNamespace { get; set; }
        public string? RepositoryInterfaceFullName =>
            RepositoryInterfaceName is null
                ? null
                : string.IsNullOrWhiteSpace(RepositoryInterfaceNamespace)
                    ? $"global::{RepositoryInterfaceName}"
                    : $"global::{RepositoryInterfaceNamespace}.{RepositoryInterfaceName}";
        public string TableName { get; set; } = string.Empty;
        public string? Schema { get; set; }
        public string? TableComment { get; set; }
        public List<CheckConstraintModel> CheckConstraints { get; set; } = new List<CheckConstraintModel>();
        public List<CompositeIndexModel> CompositeIndexes { get; set; } = new List<CompositeIndexModel>();
        public string? SoftDeleteColumn { get; set; }
        public string? TenantColumn { get; set; }
        public Location? Location { get; set; }
        public bool HasFallbackPrimaryKey { get; set; }
        public List<PropertyModel> Properties { get; set; } = new List<PropertyModel>();
        public PropertyModel? PrimaryKey { get; set; }
    }

    private class CompositeIndexModel
    {
        public string IndexName { get; set; } = string.Empty;
        public List<string> ColumnNames { get; set; } = new List<string>();
        public bool IsUnique { get; set; }
    }

    private class CheckConstraintModel
    {
        public string SqlExpression { get; set; } = string.Empty;
        public string? Name { get; set; }
    }

    private class PropertyModel
    {
        public string PropertyName { get; set; } = string.Empty;
        public string ColumnName { get; set; } = string.Empty;
        public string TypeFullName { get; set; } = string.Empty;
        public string CleanTypeName { get; set; } = string.Empty;
        public string UnderlyingClrTypeName { get; set; } = string.Empty;
        public bool IsPrimaryKey { get; set; }
        public bool IsAutoIncrement { get; set; }
        public bool IsNullable { get; set; }
        public bool IsIndexed { get; set; }
        public bool IsUniqueIndex { get; set; }
        public string? IndexName { get; set; }
        public string? CustomSqlType { get; set; }

        public int? MaxLength { get; set; }
        public bool IsFixedLength { get; set; }
        public int? Precision { get; set; }
        public int? Scale { get; set; }
        public string? DefaultValueSql { get; set; }
        public bool IsUnicode { get; set; } = true;
        public bool IsJson { get; set; }
        public bool IsConcurrencyCheck { get; set; }
        public string? Comment { get; set; }

        public string? ForeignKeyTargetTable { get; set; }
        public string? ForeignKeyTargetColumn { get; set; }
        public int? ForeignKeyOnDelete { get; set; }
        public bool IsEnum { get; set; }
        public string? JsonContextTypeFullName { get; set; }
    }

    private class RepositoryInterfaceModel
    {
        public string InterfaceName { get; set; } = string.Empty;
        public string InterfaceFullName { get; set; } = string.Empty;
        public string EntityFullName { get; set; } = string.Empty;
        public string IdTypeFullName { get; set; } = string.Empty;
        public List<MethodQueryModel> QueryMethods { get; set; } = new List<MethodQueryModel>();
    }

    private class MethodQueryModel
    {
        public string MethodName { get; set; } = string.Empty;
        public string ReturnTypeFullName { get; set; } = string.Empty;
        public bool ReturnsList { get; set; }
        public string? CustomSql { get; set; }
        public List<ParameterModel> Parameters { get; set; } = new List<ParameterModel>();
    }

    private class ParameterModel
    {
        public string Name { get; set; } = string.Empty;
        public string TypeFullName { get; set; } = string.Empty;
    }

    private class MigrationModel
    {
        public string ClassFullName { get; set; } = string.Empty;
        public string ClassName { get; set; } = string.Empty;
        public string AssemblyName { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
    }
}
