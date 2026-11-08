using System;
using System.Collections.Generic;
using System.Linq;

namespace LinKit.Data;

/// <summary>
/// Compile-time description of a <c>CHECK</c> constraint declared on a table or column.
/// </summary>
public sealed class CheckConstraintMetadata
{
    /// <summary>Boolean SQL expression the row must satisfy.</summary>
    public required string SqlExpression { get; init; }

    /// <summary>Constraint name, or <see langword="null"/> to let the provider generate one.</summary>
    public string? Name { get; init; }
}

/// <summary>
/// Compile-time description of a mapped property: the physical column plus every option used to generate DDL,
/// compose queries and read values back.
/// </summary>
public sealed class ColumnSchemaMetadata
{
    /// <summary>CLR property name.</summary>
    public required string PropertyName { get; init; }

    /// <summary>Physical column name.</summary>
    public required string ColumnName { get; init; }

    /// <summary>Full name of the CLR property type, used to pick the reader accessor.</summary>
    public required string ClrTypeName { get; init; }

    /// <summary>Whether the column is part of the primary key.</summary>
    public bool IsPrimaryKey { get; init; }

    /// <summary>Whether the database generates the value on insert.</summary>
    public bool IsAutoIncrement { get; init; }

    /// <summary>Whether the column accepts <c>NULL</c>.</summary>
    public bool IsNullable { get; init; }

    /// <summary>Whether the column has an index.</summary>
    public bool IsIndexed { get; init; }

    /// <summary>Whether the column index enforces uniqueness.</summary>
    public bool IsUniqueIndex { get; init; }

    /// <summary>Index name, or <see langword="null"/> to let the provider generate one.</summary>
    public string? IndexName { get; init; }

    /// <summary>Provider-specific SQL type that overrides the generated mapping.</summary>
    public string? CustomSqlType { get; init; }

    /// <summary>Configured maximum length, or <see langword="null"/> when none was declared.</summary>
    public int? MaxLength { get; init; }

    /// <summary>
    /// Length used for string columns. Key and indexed columns need a bounded type on SQL Server, MySQL and Oracle,
    /// so they default to 255 when no length is configured.
    /// </summary>
    public int? EffectiveMaxLength =>
        MaxLength
        ?? (
            IsPrimaryKey || IsIndexed || IsUniqueIndex || ForeignKeyTargetTable != null ? 255 : null
        );

    /// <summary>Whether the column uses a fixed-length character type such as <c>CHAR(n)</c>.</summary>
    public bool IsFixedLength { get; init; }

    /// <summary>Numeric precision, or <see langword="null"/> when not applicable.</summary>
    public int? Precision { get; init; }

    /// <summary>Numeric scale, or <see langword="null"/> when not applicable.</summary>
    public int? Scale { get; init; }

    /// <summary>Raw SQL expression used as the column default.</summary>
    public string? DefaultValueSql { get; init; }

    /// <summary>Whether string values are written and read as Unicode.</summary>
    public bool IsUnicode { get; init; } = true;

    /// <summary>Whether the property is persisted as a JSON document.</summary>
    public bool IsJson { get; init; }

    /// <summary>Whether the property is an enum stored as its underlying numeric value.</summary>
    public bool IsEnum { get; init; }

    /// <summary>Whether the column participates in optimistic concurrency checks.</summary>
    public bool IsConcurrencyCheck { get; init; }

    /// <summary>Documentation comment emitted alongside the column.</summary>
    public string? Comment { get; init; }

    /// <summary>Referenced table name, or <see langword="null"/> when the column has no foreign key.</summary>
    public string? ForeignKeyTargetTable { get; init; }

    /// <summary>Referenced column name of the foreign key.</summary>
    public string? ForeignKeyTargetColumn { get; init; }

    /// <summary>Referential action used by the foreign key, or <see langword="null"/> when there is none.</summary>
    public OnDeleteAction? ForeignKeyOnDelete { get; init; }
}

/// <summary>
/// Compile-time description of a table level index, including composite indexes.
/// </summary>
public sealed class IndexSchemaMetadata
{
    /// <summary>Index name, or <see langword="null"/> to let the provider generate one.</summary>
    public required string IndexName { get; init; }

    /// <summary>Indexed columns in key order.</summary>
    public required IReadOnlyList<string> ColumnNames { get; init; }

    /// <summary>Whether the index enforces uniqueness.</summary>
    public bool IsUnique { get; init; }
}

/// <summary>
/// Compile-time description of a mapped entity: table, columns, keys, indexes and constraints.
/// </summary>
public sealed class TableSchemaMetadata
{
    /// <summary>Physical table name.</summary>
    public required string TableName { get; init; }

    /// <summary>Owner or schema the table belongs to; <see langword="null"/> for the provider default.</summary>
    public string? Schema { get; init; }

    /// <summary>Documentation comment emitted alongside the table.</summary>
    public string? TableComment { get; init; }

    /// <summary>Entity type the table maps to.</summary>
    public required Type EntityType { get; init; }

    /// <summary>Mapped columns in generated order.</summary>
    public required IReadOnlyList<ColumnSchemaMetadata> Columns { get; init; }

    /// <summary>Table level check constraints; empty when none were declared.</summary>
    public IReadOnlyList<CheckConstraintMetadata> CheckConstraints { get; init; } =
        Array.Empty<CheckConstraintMetadata>();

    /// <summary>Table level indexes; empty when none were declared.</summary>
    public IReadOnlyList<IndexSchemaMetadata> Indexes { get; init; } =
        Array.Empty<IndexSchemaMetadata>();

    /// <summary>First column flagged as primary key, or <see langword="null"/> for keyless entities.</summary>
    public ColumnSchemaMetadata? PrimaryKey => Columns.FirstOrDefault(c => c.IsPrimaryKey);
}

/// <summary>
/// Implemented by source generator to supply compile-time schema metadata without reflection.
/// </summary>
public interface ITableSchemaProvider
{
    /// <summary>Returns the metadata of every mapped entity in the assembly.</summary>
    /// <returns>The generated table metadata.</returns>
    IReadOnlyList<TableSchemaMetadata> GetTables();
}
