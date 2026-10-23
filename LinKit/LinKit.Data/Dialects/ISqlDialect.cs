using System;
using System.Text;

namespace LinKit.Data.Dialects;

public interface ISqlDialect
{
    DatabaseDialect DialectType { get; }
    string ParameterPrefix { get; }
    string QuoteIdentifier(string identifier);
    string MapSqlType(ColumnSchemaMetadata column);

    string GenerateCreateTableSql(TableSchemaMetadata table);
    string GenerateAddColumnSql(TableSchemaMetadata table, ColumnSchemaMetadata column);
    string GenerateCreateIndexSql(TableSchemaMetadata table, ColumnSchemaMetadata column);
    string GenerateCreateCompositeIndexSql(TableSchemaMetadata table, IndexSchemaMetadata index)
    {
        var unique = index.IsUnique ? "UNIQUE " : "";
        var cols = string.Join(", ", System.Linq.Enumerable.Select(index.ColumnNames, QuoteIdentifier));
        return $"CREATE {unique}INDEX IF NOT EXISTS {QuoteIdentifier(index.IndexName)} ON {QuoteIdentifier(table.TableName)} ({cols});";
    }
    string GenerateGetExistingColumnsSql(TableSchemaMetadata table);
    string GenerateGetExistingIndexesSql(TableSchemaMetadata table);

    string GenerateCreateMigrationsTableSql();
    string GenerateRenameColumnSql(string tableName, string oldColumnName, string newColumnName);
    string GenerateDropColumnSql(string tableName, string columnName);
    string GenerateRenameTableSql(string oldTableName, string newTableName);
    string GenerateDropTableSql(string tableName);
}

public static class SqlDialectFactory
{
    public static ISqlDialect GetDialect(DatabaseDialect dialect) => dialect switch
    {
        DatabaseDialect.Sqlite => new SqliteDialect(),
        DatabaseDialect.PostgreSql => new PostgreSqlDialect(),
        DatabaseDialect.SqlServer => new SqlServerDialect(),
        DatabaseDialect.MySql => new MySqlDialect(),
        DatabaseDialect.Oracle => new OracleDialect(),
        _ => throw new NotSupportedException($"Database dialect {dialect} is not supported.")
    };
}

internal static class SqlDialectHelper
{
    public static string NormalizeClrTypeName(string clrTypeName)
    {
        if (string.IsNullOrWhiteSpace(clrTypeName)) return "";
        var clean = clrTypeName.Replace("global::", "").Trim();
        if (clean.StartsWith("System.Nullable<", StringComparison.Ordinal) && clean.EndsWith(">", StringComparison.Ordinal))
        {
            clean = clean.Substring(16, clean.Length - 17).Trim().Replace("global::", "");
        }
        else if (clean.StartsWith("Nullable<", StringComparison.Ordinal) && clean.EndsWith(">", StringComparison.Ordinal))
        {
            clean = clean.Substring(9, clean.Length - 10).Trim().Replace("global::", "");
        }
        return clean.TrimEnd('?').Trim();
    }
}

public class SqliteDialect : ISqlDialect
{
    public DatabaseDialect DialectType => DatabaseDialect.Sqlite;
    public string ParameterPrefix => "@";

    public string QuoteIdentifier(string identifier) => $"\"{identifier}\"";

    public string MapSqlType(ColumnSchemaMetadata column)
    {
        if (!string.IsNullOrEmpty(column.CustomSqlType))
            return column.CustomSqlType!;

        if (column.IsJson)
            return "TEXT";

        if (column.IsEnum)
            return "INTEGER";

        var type = SqlDialectHelper.NormalizeClrTypeName(column.ClrTypeName);
        return type switch
        {
            "System.Int32" or "int" => "INTEGER",
            "System.Int64" or "long" => "INTEGER",
            "System.Int16" or "short" => "INTEGER",
            "System.Byte" or "byte" => "INTEGER",
            "System.SByte" or "sbyte" => "INTEGER",
            "System.UInt32" or "uint" => "INTEGER",
            "System.UInt64" or "ulong" => "INTEGER",
            "System.UInt16" or "ushort" => "INTEGER",
            "System.Boolean" or "bool" => "INTEGER",
            "System.Char" or "char" => "TEXT",
            "System.Decimal" or "decimal" => "NUMERIC",
            "System.Double" or "double" => "REAL",
            "System.Single" or "float" => "REAL",
            "System.DateTime" or "System.DateTimeOffset" => "TEXT",
            "System.DateOnly" or "DateOnly" => "TEXT",
            "System.TimeOnly" or "TimeOnly" => "TEXT",
            "System.TimeSpan" or "TimeSpan" => "TEXT",
            "System.Guid" => "TEXT",
            "System.Byte[]" or "byte[]" => "BLOB",
            _ => "TEXT"
        };
    }

    public string GenerateCreateTableSql(TableSchemaMetadata table)
    {
        var sb = new StringBuilder();
        sb.Append($"CREATE TABLE IF NOT EXISTS {QuoteIdentifier(table.TableName)} (");

        for (int i = 0; i < table.Columns.Count; i++)
        {
            var col = table.Columns[i];
            if (i > 0) sb.Append(", ");
            sb.Append($"{QuoteIdentifier(col.ColumnName)} {MapSqlType(col)}");

            if (col.IsPrimaryKey)
            {
                sb.Append(" PRIMARY KEY");
                if (col.IsAutoIncrement) sb.Append(" AUTOINCREMENT");
            }
            else
            {
                if (!string.IsNullOrEmpty(col.DefaultValueSql))
                    sb.Append($" DEFAULT {col.DefaultValueSql}");

                if (!col.IsNullable)
                    sb.Append(" NOT NULL");
            }
        }

        // Foreign keys
        foreach (var col in table.Columns)
        {
            if (!string.IsNullOrEmpty(col.ForeignKeyTargetTable))
            {
                var targetCol = col.ForeignKeyTargetColumn ?? "Id";
                var onDelete = col.ForeignKeyOnDelete switch
                {
                    OnDeleteAction.Cascade => " ON DELETE CASCADE",
                    OnDeleteAction.SetNull => " ON DELETE SET NULL",
                    OnDeleteAction.Restrict => " ON DELETE RESTRICT",
                    _ => ""
                };
                sb.Append($", FOREIGN KEY ({QuoteIdentifier(col.ColumnName)}) REFERENCES {QuoteIdentifier(col.ForeignKeyTargetTable!)} ({QuoteIdentifier(targetCol)}){onDelete}");
            }
        }

        // Check constraints
        foreach (var check in table.CheckConstraints)
        {
            var name = !string.IsNullOrEmpty(check.Name) ? $"CONSTRAINT {QuoteIdentifier(check.Name!)} " : "";
            sb.Append($", {name}CHECK ({check.SqlExpression})");
        }

        sb.Append(");");
        return sb.ToString();
    }

    public string GenerateAddColumnSql(TableSchemaMetadata table, ColumnSchemaMetadata column)
    {
        var sb = new StringBuilder();
        sb.Append($"ALTER TABLE {QuoteIdentifier(table.TableName)} ADD COLUMN {QuoteIdentifier(column.ColumnName)} {MapSqlType(column)}");
        if (!string.IsNullOrEmpty(column.DefaultValueSql))
        {
            sb.Append($" DEFAULT {column.DefaultValueSql}");
        }
        else if (!column.IsNullable)
        {
            var defaultVal = GetSafeDefaultSql(column);
            sb.Append($" NOT NULL DEFAULT {defaultVal}");
        }
        sb.Append(';');
        return sb.ToString();
    }

    public string GenerateCreateIndexSql(TableSchemaMetadata table, ColumnSchemaMetadata column)
    {
        var idxName = column.IndexName ?? $"idx_{table.TableName}_{column.PropertyName.ToLowerInvariant()}";
        var unique = column.IsUniqueIndex ? "UNIQUE " : "";
        return $"CREATE {unique}INDEX IF NOT EXISTS {QuoteIdentifier(idxName)} ON {QuoteIdentifier(table.TableName)} ({QuoteIdentifier(column.ColumnName)});";
    }

    public string GenerateGetExistingColumnsSql(TableSchemaMetadata table) =>
        $"PRAGMA table_info({table.TableName});";

    public string GenerateGetExistingIndexesSql(TableSchemaMetadata table) =>
        $"SELECT name FROM pragma_index_list('{table.TableName}');";

    public string GenerateCreateMigrationsTableSql() =>
        "CREATE TABLE IF NOT EXISTS \"__linkit_migrations\" (\"MigrationId\" TEXT PRIMARY KEY, \"Description\" TEXT, \"AppliedAt\" TEXT NOT NULL, \"ExecutionTimeMs\" INTEGER NOT NULL);";

    public string GenerateRenameColumnSql(string tableName, string oldColumnName, string newColumnName) =>
        $"ALTER TABLE {QuoteIdentifier(tableName)} RENAME COLUMN {QuoteIdentifier(oldColumnName)} TO {QuoteIdentifier(newColumnName)};";

    public string GenerateDropColumnSql(string tableName, string columnName) =>
        $"ALTER TABLE {QuoteIdentifier(tableName)} DROP COLUMN {QuoteIdentifier(columnName)};";

    public string GenerateRenameTableSql(string oldTableName, string newTableName) =>
        $"ALTER TABLE {QuoteIdentifier(oldTableName)} RENAME TO {QuoteIdentifier(newTableName)};";

    public string GenerateDropTableSql(string tableName) =>
        $"DROP TABLE IF EXISTS {QuoteIdentifier(tableName)};";

    private static string GetSafeDefaultSql(ColumnSchemaMetadata column)
    {
        if (column.IsEnum) return "0";
        var type = SqlDialectHelper.NormalizeClrTypeName(column.ClrTypeName);
        return type switch
        {
            "System.Int32" or "int" or "System.Int64" or "long" or "System.Int16" or "short" or "System.Byte" or "byte" => "0",
            "System.Boolean" or "bool" => "0",
            "System.Decimal" or "decimal" or "System.Double" or "double" or "System.Single" or "float" => "0.0",
            "System.DateTime" or "System.DateTimeOffset" => "'1970-01-01 00:00:00'",
            "System.Guid" => "'00000000-0000-0000-0000-000000000000'",
            _ => "''"
        };
    }
}

public class PostgreSqlDialect : ISqlDialect
{
    public DatabaseDialect DialectType => DatabaseDialect.PostgreSql;
    public string ParameterPrefix => "@";

    public string QuoteIdentifier(string identifier) => $"\"{identifier}\"";

    public string MapSqlType(ColumnSchemaMetadata column)
    {
        if (!string.IsNullOrEmpty(column.CustomSqlType))
            return column.CustomSqlType!;

        if (column.IsJson)
            return "JSONB";

        if (column.IsEnum)
            return "INTEGER";

        var type = SqlDialectHelper.NormalizeClrTypeName(column.ClrTypeName);
        return type switch
        {
            "System.Int32" or "int" => column.IsAutoIncrement ? "SERIAL" : "INTEGER",
            "System.Int64" or "long" => column.IsAutoIncrement ? "BIGSERIAL" : "BIGINT",
            "System.Int16" or "short" => "SMALLINT",
            "System.Byte" or "byte" => "SMALLINT",
            "System.SByte" or "sbyte" => "SMALLINT",
            "System.UInt32" or "uint" => "BIGINT",
            "System.UInt64" or "ulong" => "NUMERIC(20,0)",
            "System.UInt16" or "ushort" => "INTEGER",
            "System.Boolean" or "bool" => "BOOLEAN",
            "System.Char" or "char" => "CHAR(1)",
            "System.Decimal" or "decimal" => column.Precision.HasValue && column.Precision > 0
                ? $"NUMERIC({column.Precision.Value}, {column.Scale ?? 2})"
                : "NUMERIC",
            "System.Double" or "double" => "DOUBLE PRECISION",
            "System.Single" or "float" => "REAL",
            "System.DateTime" => "TIMESTAMP",
            "System.DateTimeOffset" => "TIMESTAMPTZ",
            "System.DateOnly" or "DateOnly" => "DATE",
            "System.TimeOnly" or "TimeOnly" => "TIME",
            "System.TimeSpan" or "TimeSpan" => "INTERVAL",
            "System.Guid" => "UUID",
            "System.Byte[]" or "byte[]" => "BYTEA",
            "System.String" or "string" => column.IsFixedLength && column.MaxLength.HasValue
                ? $"CHAR({column.MaxLength.Value})"
                : (column.MaxLength.HasValue ? $"VARCHAR({column.MaxLength.Value})" : "TEXT"),
            _ => "TEXT"
        };
    }

    public string GenerateCreateTableSql(TableSchemaMetadata table)
    {
        var sb = new StringBuilder();
        var tableRef = !string.IsNullOrEmpty(table.Schema)
            ? $"{QuoteIdentifier(table.Schema!)}.{QuoteIdentifier(table.TableName)}"
            : QuoteIdentifier(table.TableName);

        sb.Append($"CREATE TABLE IF NOT EXISTS {tableRef} (");

        for (int i = 0; i < table.Columns.Count; i++)
        {
            var col = table.Columns[i];
            if (i > 0) sb.Append(", ");
            sb.Append($"{QuoteIdentifier(col.ColumnName)} {MapSqlType(col)}");

            if (col.IsPrimaryKey)
            {
                sb.Append(" PRIMARY KEY");
            }
            else
            {
                if (!string.IsNullOrEmpty(col.DefaultValueSql))
                    sb.Append($" DEFAULT {col.DefaultValueSql}");

                if (!col.IsNullable)
                    sb.Append(" NOT NULL");
            }
        }

        // Foreign keys
        foreach (var col in table.Columns)
        {
            if (!string.IsNullOrEmpty(col.ForeignKeyTargetTable))
            {
                var targetCol = col.ForeignKeyTargetColumn ?? "Id";
                var onDelete = col.ForeignKeyOnDelete switch
                {
                    OnDeleteAction.Cascade => " ON DELETE CASCADE",
                    OnDeleteAction.SetNull => " ON DELETE SET NULL",
                    OnDeleteAction.Restrict => " ON DELETE RESTRICT",
                    _ => ""
                };
                sb.Append($", FOREIGN KEY ({QuoteIdentifier(col.ColumnName)}) REFERENCES {QuoteIdentifier(col.ForeignKeyTargetTable!)} ({QuoteIdentifier(targetCol)}){onDelete}");
            }
        }

        // Check constraints
        foreach (var check in table.CheckConstraints)
        {
            var name = !string.IsNullOrEmpty(check.Name) ? $"CONSTRAINT {QuoteIdentifier(check.Name!)} " : "";
            sb.Append($", {name}CHECK ({check.SqlExpression})");
        }

        sb.Append(");");
        return sb.ToString();
    }

    public string GenerateAddColumnSql(TableSchemaMetadata table, ColumnSchemaMetadata column)
    {
        var sb = new StringBuilder();
        var tableRef = !string.IsNullOrEmpty(table.Schema)
            ? $"{QuoteIdentifier(table.Schema!)}.{QuoteIdentifier(table.TableName)}"
            : QuoteIdentifier(table.TableName);

        sb.Append($"ALTER TABLE {tableRef} ADD COLUMN IF NOT EXISTS {QuoteIdentifier(column.ColumnName)} {MapSqlType(column)}");
        if (!string.IsNullOrEmpty(column.DefaultValueSql))
        {
            sb.Append($" DEFAULT {column.DefaultValueSql}");
            if (!column.IsNullable) sb.Append(" NOT NULL");
        }
        else if (!column.IsNullable)
        {
            sb.Append($" DEFAULT {GetSafeDefaultSql(column)} NOT NULL");
        }
        sb.Append(';');
        return sb.ToString();
    }

    public string GenerateCreateIndexSql(TableSchemaMetadata table, ColumnSchemaMetadata column)
    {
        var idxName = column.IndexName ?? $"idx_{table.TableName}_{column.PropertyName.ToLowerInvariant()}";
        var unique = column.IsUniqueIndex ? "UNIQUE " : "";
        var tableRef = !string.IsNullOrEmpty(table.Schema)
            ? $"{QuoteIdentifier(table.Schema!)}.{QuoteIdentifier(table.TableName)}"
            : QuoteIdentifier(table.TableName);

        return $"CREATE {unique}INDEX IF NOT EXISTS {QuoteIdentifier(idxName)} ON {tableRef} ({QuoteIdentifier(column.ColumnName)});";
    }

    public string GenerateGetExistingColumnsSql(TableSchemaMetadata table) =>
        $"SELECT column_name FROM information_schema.columns WHERE {(string.IsNullOrEmpty(table.Schema) ? "table_schema = CURRENT_SCHEMA" : $"table_schema = '{table.Schema}'")} AND table_name = '{table.TableName}';";

    public string GenerateGetExistingIndexesSql(TableSchemaMetadata table) =>
        $"SELECT indexname FROM pg_indexes WHERE schemaname = {(string.IsNullOrEmpty(table.Schema) ? "CURRENT_SCHEMA" : $"'{table.Schema}'")} AND tablename = '{table.TableName}';";

    public string GenerateCreateMigrationsTableSql() =>
        "CREATE TABLE IF NOT EXISTS \"__linkit_migrations\" (\"MigrationId\" VARCHAR(128) PRIMARY KEY, \"Description\" VARCHAR(512), \"AppliedAt\" TIMESTAMPTZ NOT NULL, \"ExecutionTimeMs\" BIGINT NOT NULL);";

    public string GenerateRenameColumnSql(string tableName, string oldColumnName, string newColumnName) =>
        $"ALTER TABLE {QuoteIdentifier(tableName)} RENAME COLUMN {QuoteIdentifier(oldColumnName)} TO {QuoteIdentifier(newColumnName)};";

    public string GenerateDropColumnSql(string tableName, string columnName) =>
        $"ALTER TABLE {QuoteIdentifier(tableName)} DROP COLUMN IF EXISTS {QuoteIdentifier(columnName)};";

    public string GenerateRenameTableSql(string oldTableName, string newTableName) =>
        $"ALTER TABLE {QuoteIdentifier(oldTableName)} RENAME TO {QuoteIdentifier(newTableName)};";

    public string GenerateDropTableSql(string tableName) =>
        $"DROP TABLE IF EXISTS {QuoteIdentifier(tableName)} CASCADE;";

    private static string GetSafeDefaultSql(ColumnSchemaMetadata column)
    {
        if (column.IsEnum) return "0";
        var type = SqlDialectHelper.NormalizeClrTypeName(column.ClrTypeName);
        return type switch
        {
            "System.Int32" or "int" or "System.Int64" or "long" or "System.Int16" or "short" or "System.Byte" or "byte" => "0",
            "System.Boolean" or "bool" => "FALSE",
            "System.Decimal" or "decimal" or "System.Double" or "double" or "System.Single" or "float" => "0.0",
            "System.DateTime" or "System.DateTimeOffset" => "NOW()",
            "System.Guid" => "'00000000-0000-0000-0000-000000000000'",
            _ => "''"
        };
    }
}

public class SqlServerDialect : ISqlDialect
{
    public DatabaseDialect DialectType => DatabaseDialect.SqlServer;
    public string ParameterPrefix => "@";

    public string QuoteIdentifier(string identifier) => $"[{identifier}]";

    public string MapSqlType(ColumnSchemaMetadata column)
    {
        if (!string.IsNullOrEmpty(column.CustomSqlType))
            return column.CustomSqlType!;

        if (column.IsJson)
            return "NVARCHAR(MAX)";

        if (column.IsEnum)
            return "INT";

        var type = SqlDialectHelper.NormalizeClrTypeName(column.ClrTypeName);
        return type switch
        {
            "System.Int32" or "int" => column.IsAutoIncrement ? "INT IDENTITY(1,1)" : "INT",
            "System.Int64" or "long" => column.IsAutoIncrement ? "BIGINT IDENTITY(1,1)" : "BIGINT",
            "System.Int16" or "short" => "SMALLINT",
            "System.Byte" or "byte" => "TINYINT",
            "System.SByte" or "sbyte" => "SMALLINT",
            "System.UInt32" or "uint" => "BIGINT",
            "System.UInt64" or "ulong" => "DECIMAL(20,0)",
            "System.UInt16" or "ushort" => "INT",
            "System.Boolean" or "bool" => "BIT",
            "System.Char" or "char" => column.IsUnicode ? "NCHAR(1)" : "CHAR(1)",
            "System.Decimal" or "decimal" => column.Precision.HasValue && column.Precision > 0
                ? $"DECIMAL({column.Precision.Value},{column.Scale ?? 2})"
                : "DECIMAL(18,4)",
            "System.Double" or "double" => "FLOAT",
            "System.Single" or "float" => "REAL",
            "System.DateTime" => "DATETIME2",
            "System.DateTimeOffset" => "DATETIMEOFFSET",
            "System.DateOnly" or "DateOnly" => "DATE",
            "System.TimeOnly" or "TimeOnly" => "TIME",
            "System.TimeSpan" or "TimeSpan" => "TIME",
            "System.Guid" => "UNIQUEIDENTIFIER",
            "System.Byte[]" or "byte[]" => column.MaxLength.HasValue ? $"VARBINARY({column.MaxLength.Value})" : "VARBINARY(MAX)",
            "System.String" or "string" => column.IsFixedLength && column.EffectiveMaxLength.HasValue
                ? (column.IsUnicode ? $"NCHAR({column.EffectiveMaxLength.Value})" : $"CHAR({column.EffectiveMaxLength.Value})")
                : (column.EffectiveMaxLength.HasValue
                    ? (column.IsUnicode ? $"NVARCHAR({column.EffectiveMaxLength.Value})" : $"VARCHAR({column.EffectiveMaxLength.Value})")
                    : (column.IsUnicode ? "NVARCHAR(MAX)" : "VARCHAR(MAX)")),
            _ => column.IsUnicode ? "NVARCHAR(MAX)" : "VARCHAR(MAX)"
        };
    }

    public string GenerateCreateTableSql(TableSchemaMetadata table)
    {
        var sb = new StringBuilder();
        var tableRef = !string.IsNullOrEmpty(table.Schema)
            ? $"{QuoteIdentifier(table.Schema!)}.{QuoteIdentifier(table.TableName)}"
            : QuoteIdentifier(table.TableName);

        sb.AppendLine($"IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = '{table.TableName}')");
        sb.AppendLine("BEGIN");
        sb.Append($"    CREATE TABLE {tableRef} (");

        for (int i = 0; i < table.Columns.Count; i++)
        {
            var col = table.Columns[i];
            if (i > 0) sb.Append(", ");
            sb.Append($"{QuoteIdentifier(col.ColumnName)} {MapSqlType(col)}");

            if (col.IsPrimaryKey)
            {
                sb.Append(" PRIMARY KEY");
            }
            else
            {
                if (!string.IsNullOrEmpty(col.DefaultValueSql))
                    sb.Append($" DEFAULT {col.DefaultValueSql}");

                if (!col.IsNullable)
                    sb.Append(" NOT NULL");
            }
        }

        // Foreign keys
        foreach (var col in table.Columns)
        {
            if (!string.IsNullOrEmpty(col.ForeignKeyTargetTable))
            {
                var targetCol = col.ForeignKeyTargetColumn ?? "Id";
                var onDelete = col.ForeignKeyOnDelete switch
                {
                    OnDeleteAction.Cascade => " ON DELETE CASCADE",
                    OnDeleteAction.SetNull => " ON DELETE SET NULL",
                    OnDeleteAction.Restrict => " ON DELETE NO ACTION",
                    _ => ""
                };
                sb.Append($", FOREIGN KEY ({QuoteIdentifier(col.ColumnName)}) REFERENCES {QuoteIdentifier(col.ForeignKeyTargetTable!)} ({QuoteIdentifier(targetCol)}){onDelete}");
            }
        }

        // Check constraints
        foreach (var check in table.CheckConstraints)
        {
            var name = !string.IsNullOrEmpty(check.Name) ? $"CONSTRAINT {QuoteIdentifier(check.Name!)} " : "";
            sb.Append($", {name}CHECK ({check.SqlExpression})");
        }

        sb.AppendLine(");");
        sb.AppendLine("END;");
        return sb.ToString();
    }

    public string GenerateAddColumnSql(TableSchemaMetadata table, ColumnSchemaMetadata column)
    {
        var tableRef = !string.IsNullOrEmpty(table.Schema)
            ? $"{QuoteIdentifier(table.Schema!)}.{QuoteIdentifier(table.TableName)}"
            : QuoteIdentifier(table.TableName);

        var nullability = !string.IsNullOrEmpty(column.DefaultValueSql)
            ? $"DEFAULT {column.DefaultValueSql} {(column.IsNullable ? "NULL" : "NOT NULL")}"
            : (column.IsNullable ? "NULL" : $"NOT NULL DEFAULT {GetSafeDefaultSql(column)}");

        return $"IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'{table.TableName}') AND name = '{column.ColumnName}') " +
               $"ALTER TABLE {tableRef} ADD {QuoteIdentifier(column.ColumnName)} {MapSqlType(column)} {nullability};";
    }

    public string GenerateCreateIndexSql(TableSchemaMetadata table, ColumnSchemaMetadata column)
    {
        var idxName = column.IndexName ?? $"idx_{table.TableName}_{column.PropertyName.ToLowerInvariant()}";
        var unique = column.IsUniqueIndex ? "UNIQUE " : "";
        var tableRef = !string.IsNullOrEmpty(table.Schema)
            ? $"{QuoteIdentifier(table.Schema!)}.{QuoteIdentifier(table.TableName)}"
            : QuoteIdentifier(table.TableName);

        return $"IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = '{idxName}' AND object_id = OBJECT_ID(N'{table.TableName}')) " +
               $"CREATE {unique}INDEX [{idxName}] ON {tableRef} ({QuoteIdentifier(column.ColumnName)});";
    }

    public string GenerateCreateCompositeIndexSql(TableSchemaMetadata table, IndexSchemaMetadata index)
    {
        var unique = index.IsUnique ? "UNIQUE " : "";
        var tableRef = !string.IsNullOrEmpty(table.Schema)
            ? $"{QuoteIdentifier(table.Schema!)}.{QuoteIdentifier(table.TableName)}"
            : QuoteIdentifier(table.TableName);
        var cols = string.Join(", ", System.Linq.Enumerable.Select(index.ColumnNames, QuoteIdentifier));

        return $"IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = '{index.IndexName}' AND object_id = OBJECT_ID(N'{table.TableName}')) " +
               $"CREATE {unique}INDEX [{index.IndexName}] ON {tableRef} ({cols});";
    }

    public string GenerateGetExistingColumnsSql(TableSchemaMetadata table) =>
        $"SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = '{table.TableName}'{(string.IsNullOrEmpty(table.Schema) ? "" : $" AND TABLE_SCHEMA = '{table.Schema}'")};";

    public string GenerateGetExistingIndexesSql(TableSchemaMetadata table)
    {
        var tableRef = !string.IsNullOrEmpty(table.Schema) ? $"{table.Schema}.{table.TableName}" : table.TableName;
        return $"SELECT name FROM sys.indexes WHERE object_id = OBJECT_ID(N'{tableRef}') AND name IS NOT NULL;";
    }

    public string GenerateCreateMigrationsTableSql() =>
        "IF OBJECT_ID(N'__linkit_migrations', N'U') IS NULL CREATE TABLE [__linkit_migrations] ([MigrationId] NVARCHAR(128) PRIMARY KEY, [Description] NVARCHAR(512), [AppliedAt] DATETIME2 NOT NULL, [ExecutionTimeMs] BIGINT NOT NULL);";

    public string GenerateRenameColumnSql(string tableName, string oldColumnName, string newColumnName) =>
        $"EXEC sp_rename '{tableName}.[{oldColumnName}]', '{newColumnName}', 'COLUMN';";

    public string GenerateDropColumnSql(string tableName, string columnName) =>
        $"ALTER TABLE [{tableName}] DROP COLUMN [{columnName}];";

    public string GenerateRenameTableSql(string oldTableName, string newTableName) =>
        $"EXEC sp_rename '{oldTableName}', '{newTableName}';";

    public string GenerateDropTableSql(string tableName) =>
        $"IF OBJECT_ID(N'{tableName}', N'U') IS NOT NULL DROP TABLE [{tableName}];";

    private static string GetSafeDefaultSql(ColumnSchemaMetadata column)
    {
        if (column.IsEnum) return "0";
        var type = SqlDialectHelper.NormalizeClrTypeName(column.ClrTypeName);
        return type switch
        {
            "System.Int32" or "int" or "System.Int64" or "long" or "System.Int16" or "short" or "System.Byte" or "byte" => "0",
            "System.Boolean" or "bool" => "0",
            "System.Decimal" or "decimal" or "System.Double" or "double" or "System.Single" or "float" => "0.0",
            "System.DateTime" or "System.DateTimeOffset" => "SYSUTCDATETIME()",
            "System.Guid" => "NEWID()",
            _ => "''"
        };
    }
}

public class MySqlDialect : ISqlDialect
{
    public DatabaseDialect DialectType => DatabaseDialect.MySql;
    public string ParameterPrefix => "@";

    public string QuoteIdentifier(string identifier) => $"`{identifier}`";

    public string MapSqlType(ColumnSchemaMetadata column)
    {
        if (!string.IsNullOrEmpty(column.CustomSqlType))
            return column.CustomSqlType!;

        if (column.IsJson)
            return "JSON";

        if (column.IsEnum)
            return "INT";

        var type = SqlDialectHelper.NormalizeClrTypeName(column.ClrTypeName);
        return type switch
        {
            "System.Int32" or "int" => column.IsAutoIncrement ? "INT AUTO_INCREMENT" : "INT",
            "System.Int64" or "long" => column.IsAutoIncrement ? "BIGINT AUTO_INCREMENT" : "BIGINT",
            "System.Int16" or "short" => "SMALLINT",
            "System.Byte" or "byte" => "TINYINT",
            "System.SByte" or "sbyte" => "TINYINT",
            "System.UInt32" or "uint" => "INT UNSIGNED",
            "System.UInt64" or "ulong" => "BIGINT UNSIGNED",
            "System.UInt16" or "ushort" => "SMALLINT UNSIGNED",
            "System.Boolean" or "bool" => "TINYINT(1)",
            "System.Char" or "char" => "CHAR(1)",
            "System.Decimal" or "decimal" => column.Precision.HasValue && column.Precision > 0
                ? $"DECIMAL({column.Precision.Value},{column.Scale ?? 2})"
                : "DECIMAL(18,4)",
            "System.Double" or "double" => "DOUBLE",
            "System.Single" or "float" => "FLOAT",
            "System.DateTime" or "System.DateTimeOffset" => "DATETIME",
            "System.DateOnly" or "DateOnly" => "DATE",
            "System.TimeOnly" or "TimeOnly" => "TIME",
            "System.TimeSpan" or "TimeSpan" => "TIME",
            "System.Guid" => "CHAR(36)",
            "System.Byte[]" or "byte[]" => column.MaxLength.HasValue ? $"VARBINARY({column.MaxLength.Value})" : "LONGBLOB",
            "System.String" or "string" => column.IsFixedLength && column.EffectiveMaxLength.HasValue
                ? $"CHAR({column.EffectiveMaxLength.Value})"
                : (column.EffectiveMaxLength.HasValue ? $"VARCHAR({column.EffectiveMaxLength.Value})" : "LONGTEXT"),
            _ => "LONGTEXT"
        };
    }

    public string GenerateCreateTableSql(TableSchemaMetadata table)
    {
        var sb = new StringBuilder();
        sb.Append($"CREATE TABLE IF NOT EXISTS {QuoteIdentifier(table.TableName)} (");

        for (int i = 0; i < table.Columns.Count; i++)
        {
            var col = table.Columns[i];
            if (i > 0) sb.Append(", ");
            sb.Append($"{QuoteIdentifier(col.ColumnName)} {MapSqlType(col)}");

            if (col.IsPrimaryKey)
            {
                sb.Append(" PRIMARY KEY");
            }
            else
            {
                if (!string.IsNullOrEmpty(col.DefaultValueSql))
                    sb.Append($" DEFAULT {col.DefaultValueSql}");

                if (!col.IsNullable)
                    sb.Append(" NOT NULL");
            }
        }

        // Foreign keys
        foreach (var col in table.Columns)
        {
            if (!string.IsNullOrEmpty(col.ForeignKeyTargetTable))
            {
                var targetCol = col.ForeignKeyTargetColumn ?? "Id";
                var onDelete = col.ForeignKeyOnDelete switch
                {
                    OnDeleteAction.Cascade => " ON DELETE CASCADE",
                    OnDeleteAction.SetNull => " ON DELETE SET NULL",
                    OnDeleteAction.Restrict => " ON DELETE RESTRICT",
                    _ => ""
                };
                sb.Append($", FOREIGN KEY ({QuoteIdentifier(col.ColumnName)}) REFERENCES {QuoteIdentifier(col.ForeignKeyTargetTable!)} ({QuoteIdentifier(targetCol)}){onDelete}");
            }
        }

        // Check constraints
        foreach (var check in table.CheckConstraints)
        {
            var name = !string.IsNullOrEmpty(check.Name) ? $"CONSTRAINT {QuoteIdentifier(check.Name!)} " : "";
            sb.Append($", {name}CHECK ({check.SqlExpression})");
        }

        sb.Append(") ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;");
        return sb.ToString();
    }

    public string GenerateAddColumnSql(TableSchemaMetadata table, ColumnSchemaMetadata column)
    {
        var sb = new StringBuilder();
        sb.Append($"ALTER TABLE {QuoteIdentifier(table.TableName)} ADD COLUMN {QuoteIdentifier(column.ColumnName)} {MapSqlType(column)}");
        if (!string.IsNullOrEmpty(column.DefaultValueSql))
        {
            sb.Append($" DEFAULT {column.DefaultValueSql}");
            if (!column.IsNullable) sb.Append(" NOT NULL");
        }
        else if (!column.IsNullable)
        {
            sb.Append($" NOT NULL DEFAULT {GetSafeDefaultSql(column)}");
        }
        sb.Append(';');
        return sb.ToString();
    }

    public string GenerateCreateIndexSql(TableSchemaMetadata table, ColumnSchemaMetadata column)
    {
        var idxName = column.IndexName ?? $"idx_{table.TableName}_{column.PropertyName.ToLowerInvariant()}";
        var unique = column.IsUniqueIndex ? "UNIQUE " : "";
        return $"CREATE {unique}INDEX `{idxName}` ON {QuoteIdentifier(table.TableName)} ({QuoteIdentifier(column.ColumnName)});";
    }

    public string GenerateGetExistingColumnsSql(TableSchemaMetadata table) =>
        $"SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{table.TableName}';";

    public string GenerateGetExistingIndexesSql(TableSchemaMetadata table) =>
        $"SELECT DISTINCT INDEX_NAME FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{table.TableName}';";

    public string GenerateCreateMigrationsTableSql() =>
        "CREATE TABLE IF NOT EXISTS `__linkit_migrations` (`MigrationId` VARCHAR(128) PRIMARY KEY, `Description` VARCHAR(512), `AppliedAt` DATETIME(6) NOT NULL, `ExecutionTimeMs` BIGINT NOT NULL);";

    public string GenerateRenameColumnSql(string tableName, string oldColumnName, string newColumnName) =>
        $"ALTER TABLE `{tableName}` RENAME COLUMN `{oldColumnName}` TO `{newColumnName}`;";

    public string GenerateDropColumnSql(string tableName, string columnName) =>
        $"ALTER TABLE `{tableName}` DROP COLUMN `{columnName}`;";

    public string GenerateRenameTableSql(string oldTableName, string newTableName) =>
        $"RENAME TABLE `{oldTableName}` TO `{newTableName}`;";

    public string GenerateDropTableSql(string tableName) =>
        $"DROP TABLE IF EXISTS `{tableName}`;";

    private static string GetSafeDefaultSql(ColumnSchemaMetadata column)
    {
        if (column.IsEnum) return "0";
        var type = SqlDialectHelper.NormalizeClrTypeName(column.ClrTypeName);
        return type switch
        {
            "System.Int32" or "int" or "System.Int64" or "long" or "System.Int16" or "short" or "System.Byte" or "byte" => "0",
            "System.Boolean" or "bool" => "0",
            "System.Decimal" or "decimal" or "System.Double" or "double" or "System.Single" or "float" => "0.0",
            "System.DateTime" or "System.DateTimeOffset" => "'1970-01-01 00:00:00'",
            "System.Guid" => "'00000000-0000-0000-0000-000000000000'",
            _ => "''"
        };
    }
}

public class OracleDialect : ISqlDialect
{
    public DatabaseDialect DialectType => DatabaseDialect.Oracle;
    public string ParameterPrefix => ":";

    public string QuoteIdentifier(string identifier) => $"\"{identifier}\"";

    public string MapSqlType(ColumnSchemaMetadata column)
    {
        if (!string.IsNullOrEmpty(column.CustomSqlType))
            return column.CustomSqlType!;

        if (column.IsJson)
            return "NCLOB";

        if (column.IsEnum)
            return "NUMBER(10)";

        var type = SqlDialectHelper.NormalizeClrTypeName(column.ClrTypeName);
        return type switch
        {
            "System.Int32" or "int" => "NUMBER(10)",
            "System.Int64" or "long" => "NUMBER(19)",
            "System.Int16" or "short" => "NUMBER(5)",
            "System.Byte" or "byte" => "NUMBER(3)",
            "System.SByte" or "sbyte" => "NUMBER(3)",
            "System.UInt32" or "uint" => "NUMBER(10)",
            "System.UInt64" or "ulong" => "NUMBER(20)",
            "System.UInt16" or "ushort" => "NUMBER(5)",
            "System.Boolean" or "bool" => "NUMBER(1)",
            "System.Char" or "char" => column.IsUnicode ? "NCHAR(1)" : "CHAR(1)",
            "System.Decimal" or "decimal" => column.Precision.HasValue && column.Precision > 0
                ? $"NUMBER({column.Precision.Value}, {column.Scale ?? 2})"
                : "NUMBER(18, 2)",
            "System.Double" or "double" => "BINARY_DOUBLE",
            "System.Single" or "float" => "BINARY_FLOAT",
            "System.DateTime" => "TIMESTAMP",
            "System.DateTimeOffset" => "TIMESTAMP WITH TIME ZONE",
            "System.DateOnly" or "DateOnly" => "DATE",
            "System.TimeOnly" or "TimeOnly" => "VARCHAR2(16)",
            "System.TimeSpan" or "TimeSpan" => "INTERVAL DAY TO SECOND",
            "System.Guid" => "VARCHAR2(36)",
            "System.Byte[]" or "byte[]" => "BLOB",
            "System.String" or "string" => column.IsUnicode
                ? (column.IsFixedLength && column.EffectiveMaxLength.HasValue
                    ? $"NCHAR({column.EffectiveMaxLength.Value})"
                    : (column.EffectiveMaxLength.HasValue ? $"NVARCHAR2({column.EffectiveMaxLength.Value})" : "NCLOB"))
                : (column.IsFixedLength && column.EffectiveMaxLength.HasValue
                    ? $"CHAR({column.EffectiveMaxLength.Value})"
                    : (column.EffectiveMaxLength.HasValue ? $"VARCHAR2({column.EffectiveMaxLength.Value})" : "CLOB")),
            _ => "NCLOB"
        };
    }

    public string GenerateCreateTableSql(TableSchemaMetadata table)
    {
        var sb = new StringBuilder();
        var tableRef = !string.IsNullOrEmpty(table.Schema)
            ? $"{QuoteIdentifier(table.Schema!)}.{QuoteIdentifier(table.TableName)}"
            : QuoteIdentifier(table.TableName);

        sb.Append("BEGIN EXECUTE IMMEDIATE 'CREATE TABLE ");
        sb.Append(tableRef.Replace("'", "''"));
        sb.Append(" (");

        for (int i = 0; i < table.Columns.Count; i++)
        {
            var col = table.Columns[i];
            if (i > 0) sb.Append(", ");
            sb.Append($"{QuoteIdentifier(col.ColumnName)} {MapSqlType(col)}");

            if (col.IsPrimaryKey)
            {
                if (col.IsAutoIncrement)
                {
                    sb.Append(" GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY");
                }
                else
                {
                    sb.Append(" PRIMARY KEY");
                }
            }
            else
            {
                if (!string.IsNullOrEmpty(col.DefaultValueSql))
                    sb.Append($" DEFAULT {col.DefaultValueSql}");

                if (!col.IsNullable)
                    sb.Append(" NOT NULL");
            }
        }

        // Foreign keys
        foreach (var col in table.Columns)
        {
            if (!string.IsNullOrEmpty(col.ForeignKeyTargetTable))
            {
                var targetCol = col.ForeignKeyTargetColumn ?? "Id";
                var onDelete = col.ForeignKeyOnDelete switch
                {
                    OnDeleteAction.Cascade => " ON DELETE CASCADE",
                    OnDeleteAction.SetNull => " ON DELETE SET NULL",
                    _ => ""
                };
                sb.Append($", FOREIGN KEY ({QuoteIdentifier(col.ColumnName)}) REFERENCES {QuoteIdentifier(col.ForeignKeyTargetTable!)} ({QuoteIdentifier(targetCol)}){onDelete}");
            }
        }

        // Check constraints
        foreach (var check in table.CheckConstraints)
        {
            var name = !string.IsNullOrEmpty(check.Name) ? $"CONSTRAINT {QuoteIdentifier(check.Name!)} " : "";
            sb.Append($", {name}CHECK ({check.SqlExpression.Replace("'", "''")})");
        }

        sb.Append(")'; EXCEPTION WHEN OTHERS THEN IF SQLCODE != -955 THEN RAISE; END IF; END;");
        return sb.ToString();
    }

    public string GenerateAddColumnSql(TableSchemaMetadata table, ColumnSchemaMetadata column)
    {
        var tableRef = !string.IsNullOrEmpty(table.Schema)
            ? $"{QuoteIdentifier(table.Schema!)}.{QuoteIdentifier(table.TableName)}"
            : QuoteIdentifier(table.TableName);

        var sb = new StringBuilder();
        sb.Append($"ALTER TABLE {tableRef} ADD ({QuoteIdentifier(column.ColumnName)} {MapSqlType(column)}");
        if (!string.IsNullOrEmpty(column.DefaultValueSql))
        {
            sb.Append($" DEFAULT {column.DefaultValueSql}");
            if (!column.IsNullable) sb.Append(" NOT NULL");
        }
        else if (!column.IsNullable)
        {
            sb.Append($" DEFAULT {GetSafeDefaultSql(column)} NOT NULL");
        }
        sb.Append(")");
        return sb.ToString();
    }

    public string GenerateCreateIndexSql(TableSchemaMetadata table, ColumnSchemaMetadata column)
    {
        var tableRef = !string.IsNullOrEmpty(table.Schema)
            ? $"{QuoteIdentifier(table.Schema!)}.{QuoteIdentifier(table.TableName)}"
            : QuoteIdentifier(table.TableName);

        var idxName = column.IndexName ?? $"idx_{table.TableName}_{column.PropertyName.ToLowerInvariant()}";
        var unique = column.IsUniqueIndex ? "UNIQUE " : "";
        return $"BEGIN EXECUTE IMMEDIATE 'CREATE {unique}INDEX \"{idxName}\" ON {tableRef.Replace("'", "''")} (\"{column.ColumnName}\")'; EXCEPTION WHEN OTHERS THEN IF SQLCODE != -955 AND SQLCODE != -1408 THEN RAISE; END IF; END;";
    }

    public string GenerateGetExistingColumnsSql(TableSchemaMetadata table) =>
        $"SELECT COLUMN_NAME FROM ALL_TAB_COLS WHERE {GetOwnerPredicate(table.Schema)} AND TABLE_NAME = '{EscapeMetadataLiteral(table.TableName)}';";

    public string GenerateGetExistingIndexesSql(TableSchemaMetadata table) =>
        $"SELECT INDEX_NAME FROM ALL_INDEXES WHERE {GetOwnerPredicate(table.Schema)} AND TABLE_NAME = '{EscapeMetadataLiteral(table.TableName)}';";

    private static string EscapeMetadataLiteral(string value) => value.Replace("'", "''");
    private static string GetOwnerPredicate(string? schema) =>
        string.IsNullOrEmpty(schema)
            ? "OWNER = SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA')"
            : $"OWNER = '{EscapeMetadataLiteral(schema)}'";

    public string GenerateCreateMigrationsTableSql() =>
        "BEGIN EXECUTE IMMEDIATE 'CREATE TABLE \"__linkit_migrations\" (\"MigrationId\" VARCHAR2(128) PRIMARY KEY, \"Description\" VARCHAR2(512), \"AppliedAt\" TIMESTAMP WITH TIME ZONE NOT NULL, \"ExecutionTimeMs\" NUMBER(19) NOT NULL)'; EXCEPTION WHEN OTHERS THEN IF SQLCODE != -955 THEN RAISE; END IF; END;";

    public string GenerateRenameColumnSql(string tableName, string oldColumnName, string newColumnName) =>
        $"ALTER TABLE {QuoteIdentifier(tableName)} RENAME COLUMN {QuoteIdentifier(oldColumnName)} TO {QuoteIdentifier(newColumnName)}";

    public string GenerateDropColumnSql(string tableName, string columnName) =>
        $"ALTER TABLE {QuoteIdentifier(tableName)} DROP COLUMN {QuoteIdentifier(columnName)}";

    public string GenerateRenameTableSql(string oldTableName, string newTableName) =>
        $"RENAME {QuoteIdentifier(oldTableName)} TO {QuoteIdentifier(newTableName)}";

    public string GenerateDropTableSql(string tableName) =>
        $"BEGIN EXECUTE IMMEDIATE 'DROP TABLE {QuoteIdentifier(tableName)} CASCADE CONSTRAINTS PURGE'; EXCEPTION WHEN OTHERS THEN IF SQLCODE != -942 THEN RAISE; END IF; END;";

    private static string GetSafeDefaultSql(ColumnSchemaMetadata column)
    {
        if (column.IsEnum) return "0";
        var type = SqlDialectHelper.NormalizeClrTypeName(column.ClrTypeName);
        return type switch
        {
            "System.Int32" or "int" or "System.Int64" or "long" or "System.Int16" or "short" or "System.Byte" or "byte" => "0",
            "System.Boolean" or "bool" => "0",
            "System.Decimal" or "decimal" or "System.Double" or "double" or "System.Single" or "float" => "0.0",
            "System.DateTime" or "System.DateTimeOffset" => "TIMESTAMP '1970-01-01 00:00:00'",
            "System.Guid" => "'00000000-0000-0000-0000-000000000000'",
            _ => "''"
        };
    }
}
