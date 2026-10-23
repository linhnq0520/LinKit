using System;
using System.Collections.Generic;
using System.Linq;

namespace LinKit.Data;

public sealed class CheckConstraintMetadata
{
    public required string SqlExpression { get; init; }
    public string? Name { get; init; }
}

public sealed class ColumnSchemaMetadata
{
    public required string PropertyName { get; init; }
    public required string ColumnName { get; init; }
    public required string ClrTypeName { get; init; }
    public bool IsPrimaryKey { get; init; }
    public bool IsAutoIncrement { get; init; }
    public bool IsNullable { get; init; }
    public bool IsIndexed { get; init; }
    public bool IsUniqueIndex { get; init; }
    public string? IndexName { get; init; }
    public string? CustomSqlType { get; init; }

    public int? MaxLength { get; init; }

    /// <summary>
    /// Length used for string columns. Key and indexed columns need a bounded type on SQL Server, MySQL and Oracle,
    /// so they default to 255 when no length is configured.
    /// </summary>
    public int? EffectiveMaxLength => MaxLength ?? (IsPrimaryKey || IsIndexed || IsUniqueIndex || ForeignKeyTargetTable != null ? 255 : null);
    public bool IsFixedLength { get; init; }
    public int? Precision { get; init; }
    public int? Scale { get; init; }
    public string? DefaultValueSql { get; init; }
    public bool IsUnicode { get; init; } = true;
    public bool IsJson { get; init; }
    public bool IsEnum { get; init; }
    public bool IsConcurrencyCheck { get; init; }
    public string? Comment { get; init; }

    public string? ForeignKeyTargetTable { get; init; }
    public string? ForeignKeyTargetColumn { get; init; }
    public OnDeleteAction? ForeignKeyOnDelete { get; init; }
}

public sealed class IndexSchemaMetadata
{
    public required string IndexName { get; init; }
    public required IReadOnlyList<string> ColumnNames { get; init; }
    public bool IsUnique { get; init; }
}

public sealed class TableSchemaMetadata
{
    public required string TableName { get; init; }
    public string? Schema { get; init; }
    public string? TableComment { get; init; }
    public required Type EntityType { get; init; }
    public required IReadOnlyList<ColumnSchemaMetadata> Columns { get; init; }
    public IReadOnlyList<CheckConstraintMetadata> CheckConstraints { get; init; } = Array.Empty<CheckConstraintMetadata>();
    public IReadOnlyList<IndexSchemaMetadata> Indexes { get; init; } = Array.Empty<IndexSchemaMetadata>();
    public ColumnSchemaMetadata? PrimaryKey => Columns.FirstOrDefault(c => c.IsPrimaryKey);
}

/// <summary>
/// Implemented by source generator to supply compile-time schema metadata without reflection.
/// </summary>
public interface ITableSchemaProvider
{
    IReadOnlyList<TableSchemaMetadata> GetTables();
}

