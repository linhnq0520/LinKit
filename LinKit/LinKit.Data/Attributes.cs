using System;

namespace LinKit.Data;

/// <summary>
/// Referential integrity action on foreign key deletion.
/// </summary>
public enum OnDeleteAction
{
    NoAction,
    Cascade,
    SetNull,
    Restrict
}

/// <summary>
/// Marks a class as a database table entity.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class TableAttribute : Attribute
{
    public string Name { get; }
    public string? Schema { get; set; }
    public string? Comment { get; set; }

    public TableAttribute(string name)
    {
        Name = name;
    }
}

/// <summary>
/// Marks a property as the Primary Key of the table.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
public sealed class PrimaryKeyAttribute : Attribute
{
    public bool AutoIncrement { get; set; } = false;

    public PrimaryKeyAttribute() { }

    public PrimaryKeyAttribute(bool autoIncrement)
    {
        AutoIncrement = autoIncrement;
    }
}

/// <summary>
/// Customizes column mapping for a property.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
public sealed class ColumnAttribute : Attribute
{
    public string? Name { get; }
    public string? CustomSqlType { get; set; }
    public int Length { get; set; } = 0;
    public int Precision { get; set; } = 0;
    public int Scale { get; set; } = 0;
    public bool IsNullable { get; set; } = true;
    public string? Comment { get; set; }

    /// <summary>
    /// Column position hint used when generating CREATE TABLE, INSERT and reader mapping.
    /// Default 0. Negative values go first, positive values go last (e.g. audit columns).
    /// Columns with equal Order keep base-class-first, declaration order. Only affects newly created tables.
    /// </summary>
    public int Order { get; set; } = 0;

    public ColumnAttribute() { }

    public ColumnAttribute(string name)
    {
        Name = name;
    }
}

/// <summary>
/// Configures maximum length for string or binary columns (VARCHAR(n), NVARCHAR(n), VARBINARY(n)).
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
public sealed class MaxLengthAttribute : Attribute
{
    public int Length { get; }

    public MaxLengthAttribute(int length)
    {
        Length = length;
    }
}

/// <summary>
/// Configures fixed length for character columns (CHAR(n), NCHAR(n)).
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
public sealed class FixedLengthAttribute : Attribute
{
    public int Length { get; }

    public FixedLengthAttribute(int length)
    {
        Length = length;
    }
}

/// <summary>
/// Configures precision and scale for decimal and numeric columns: DECIMAL(precision, scale).
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
public sealed class DecimalPrecisionAttribute : Attribute
{
    public int Precision { get; }
    public int Scale { get; }

    public DecimalPrecisionAttribute(int precision, int scale = 2)
    {
        Precision = precision;
        Scale = scale;
    }
}

/// <summary>
/// Sets a raw SQL default value expression during column creation.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
public sealed class DefaultValueSqlAttribute : Attribute
{
    public string SqlExpression { get; }

    public DefaultValueSqlAttribute(string sqlExpression)
    {
        SqlExpression = sqlExpression;
    }
}

/// <summary>
/// Configures foreign key constraint linking to another table.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
public sealed class ForeignKeyAttribute : Attribute
{
    public string TargetTable { get; }
    public string TargetColumn { get; set; } = "Id";
    public OnDeleteAction OnDelete { get; set; } = OnDeleteAction.Cascade;

    public ForeignKeyAttribute(string targetTable)
    {
        TargetTable = targetTable;
    }
}

/// <summary>
/// Configures database CHECK constraint for table or column.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property, Inherited = true, AllowMultiple = true)]
public sealed class CheckConstraintAttribute : Attribute
{
    public string SqlExpression { get; }
    public string? Name { get; set; }

    public CheckConstraintAttribute(string sqlExpression)
    {
        SqlExpression = sqlExpression;
    }
}

/// <summary>
/// Adds database documentation comment to table or column.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
public sealed class CommentAttribute : Attribute
{
    public string Text { get; }

    public CommentAttribute(string text)
    {
        Text = text;
    }
}

/// <summary>
/// Specifies whether a string column supports unicode (NVARCHAR vs VARCHAR). Default true.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
public sealed class UnicodeAttribute : Attribute
{
    public bool IsUnicode { get; }

    public UnicodeAttribute(bool isUnicode = true)
    {
        IsUnicode = isUnicode;
    }
}

/// <summary>
/// Serializes a complex object/collection into JSON/JSONB database column.
/// Supports Native AOT via System.Text.Json source generators (JsonSerializerContext).
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
public sealed class JsonColumnAttribute : Attribute
{
    /// <summary>
    /// Optional custom JsonSerializerContext type for compile-time Native AOT serialization.
    /// If null, LinKit.Data automatically generates a compile-time JsonSerializerContext for this type.
    /// </summary>
    public Type? JsonSerializerContextType { get; }

    public JsonColumnAttribute() { }

    public JsonColumnAttribute(Type jsonSerializerContextType)
    {
        JsonSerializerContextType = jsonSerializerContextType;
    }
}

/// <summary>
/// Marks a column for optimistic concurrency token tracking (RowVersion / Timestamp).
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
public sealed class ConcurrencyCheckAttribute : Attribute
{
}

/// <summary>
/// Excludes a property from being mapped to database column.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
public sealed class IgnoreColumnAttribute : Attribute
{
}

/// <summary>
/// Creates an index for this column in the database during schema synchronization.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
public sealed class IndexedAttribute : Attribute
{
    public bool IsUnique { get; set; } = false;
    public string? IndexName { get; set; }

    public IndexedAttribute() { }

    public IndexedAttribute(bool isUnique)
    {
        IsUnique = isUnique;
    }
}

/// <summary>
/// Marks an interface for automatic source-generated concrete repository implementation.
/// </summary>
[AttributeUsage(AttributeTargets.Interface, Inherited = false, AllowMultiple = false)]
public sealed class DataRepositoryAttribute : Attribute
{
}

/// <summary>
/// Custom SQL query method annotation inside a [DataRepository] interface.
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class SqlQueryAttribute : Attribute
{
    public string Sql { get; }

    public SqlQueryAttribute(string sql)
    {
        Sql = sql;
    }
}

/// <summary>
/// Configures a composite (multi-column) index on the entity table.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = true)]
public sealed class CompositeIndexAttribute : Attribute
{
    public string Name { get; }
    public string[] ColumnNames { get; }
    public bool IsUnique { get; set; } = false;

    public CompositeIndexAttribute(string name, params string[] columnNames)
    {
        Name = name;
        ColumnNames = columnNames;
    }
}

/// <summary>
/// Configures soft delete support on the entity.
/// When applied, DeleteAsync performs a soft delete (UPDATE SET DeletedAt = CURRENT_TIMESTAMP),
/// and repository query methods automatically filter out soft-deleted records.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
public sealed class SoftDeleteAttribute : Attribute
{
    public string ColumnName { get; }

    public SoftDeleteAttribute(string columnName = "DeletedAt")
    {
        ColumnName = columnName;
    }
}

/// <summary>
/// Declares the tenant column name as metadata for an entity.
/// </summary>
/// <remarks>
/// This attribute does not currently add tenant predicates to generated repository queries.
/// Do not rely on it to enforce tenant isolation; add explicit tenant filters.
/// The source generator emits a warning when this attribute is used.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
public sealed class MultiTenantAttribute : Attribute
{
    public string TenantColumnName { get; }

    public MultiTenantAttribute(string tenantColumnName = "TenantId")
    {
        TenantColumnName = tenantColumnName;
    }
}
