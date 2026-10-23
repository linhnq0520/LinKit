using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using LinKit.Data.Dialects;
using Microsoft.Extensions.DependencyInjection;

namespace LinKit.Data.Migrations;

/// <summary>
/// Execution context implementation passed to IDataMigration during UpAsync / DownAsync.
/// </summary>
public sealed class MigrationContext : IMigrationContext
{
    private readonly ISqlDialect _sqlDialect;

    public DbConnection Connection { get; }
    public DbTransaction? Transaction { get; }
    public DatabaseDialect Dialect { get; }
    public IServiceProvider Services { get; }

    public MigrationContext(
        DbConnection connection,
        DbTransaction? transaction,
        DatabaseDialect dialect,
        IServiceProvider services)
    {
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));
        Transaction = transaction;
        Dialect = dialect;
        Services = services ?? throw new ArgumentNullException(nameof(services));
        _sqlDialect = SqlDialectFactory.GetDialect(dialect);
    }

    public async Task<int> ExecuteSqlAsync(string sql, object? parameters = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sql)) return 0;

        await using var cmd = Connection.CreateCommand();
        cmd.Transaction = Transaction;
        cmd.CommandText = sql;
        AttachParameters(cmd, parameters);

        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<T>> QuerySqlAsync<T>(string sql, Func<DbDataReader, T> map, object? parameters = null, CancellationToken ct = default)
    {
        await using var cmd = Connection.CreateCommand();
        cmd.Transaction = Transaction;
        cmd.CommandText = sql;
        AttachParameters(cmd, parameters);

        var list = new List<T>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(map(reader));
        }
        return list;
    }

    public async Task<object?> ExecuteScalarAsync(string sql, object? parameters = null, CancellationToken ct = default)
    {
        await using var cmd = Connection.CreateCommand();
        cmd.Transaction = Transaction;
        cmd.CommandText = sql;
        AttachParameters(cmd, parameters);

        var res = await cmd.ExecuteScalarAsync(ct);
        return res == DBNull.Value ? null : res;
    }

    public async Task<bool> ExistsAsync(string tableName, string whereCondition, object? parameters = null, CancellationToken ct = default)
    {
        var quotedTable = _sqlDialect.QuoteIdentifier(tableName);
        var whereClause = !string.IsNullOrWhiteSpace(whereCondition) ? $" WHERE {whereCondition}" : "";
        var sql = Dialect switch
        {
            DatabaseDialect.SqlServer => $"SELECT TOP 1 1 FROM {quotedTable}{whereClause}",
            DatabaseDialect.Oracle => $"SELECT 1 FROM {quotedTable}{whereClause} FETCH FIRST 1 ROWS ONLY",
            _ => $"SELECT 1 FROM {quotedTable}{whereClause} LIMIT 1"
        };

        var res = await ExecuteScalarAsync(sql, parameters, ct);
        return res != null;
    }

    public async Task<bool> ExistsAsync<TEntity>(System.Linq.Expressions.Expression<Func<TEntity, bool>> predicate, CancellationToken ct = default) where TEntity : class
    {
        var schemaProvider = Services.GetService<ITableSchemaProvider>();
        var table = schemaProvider?.GetTables().FirstOrDefault(t => t.EntityType == typeof(TEntity));
        var tableName = table?.TableName ?? typeof(TEntity).Name;

        var propToCol = table != null
            ? System.Linq.Enumerable.ToDictionary(table.Columns, c => c.PropertyName, c => c.ColumnName, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var translator = new LinKit.Data.Linq.ExpressionSqlTranslator(Dialect, propToCol);
        var translated = translator.Translate(predicate);

        var quotedTable = _sqlDialect.QuoteIdentifier(tableName);
        var whereClause = !string.IsNullOrWhiteSpace(translated.WhereSql) ? $" WHERE {translated.WhereSql}" : "";

        var sql = Dialect switch
        {
            DatabaseDialect.SqlServer => $"SELECT TOP 1 1 FROM {quotedTable}{whereClause}",
            DatabaseDialect.Oracle => $"SELECT 1 FROM {quotedTable}{whereClause} FETCH FIRST 1 ROWS ONLY",
            _ => $"SELECT 1 FROM {quotedTable}{whereClause} LIMIT 1"
        };

        var res = await ExecuteScalarAsync(sql, translated.Parameters, ct);
        return res != null;
    }

    public IRepository<TEntity, TId> GetRepository<TEntity, TId>() where TEntity : class
    {
        return Services.GetRequiredService<IRepository<TEntity, TId>>();
    }

    public Task RenameColumnAsync(string tableName, string oldColumnName, string newColumnName, CancellationToken ct = default)
    {
        var sql = _sqlDialect.GenerateRenameColumnSql(tableName, oldColumnName, newColumnName);
        return ExecuteSqlAsync(sql, null, ct);
    }

    public Task DropColumnAsync(string tableName, string columnName, CancellationToken ct = default)
    {
        var sql = _sqlDialect.GenerateDropColumnSql(tableName, columnName);
        return ExecuteSqlAsync(sql, null, ct);
    }

    public Task RenameTableAsync(string oldTableName, string newTableName, CancellationToken ct = default)
    {
        var sql = _sqlDialect.GenerateRenameTableSql(oldTableName, newTableName);
        return ExecuteSqlAsync(sql, null, ct);
    }

    public Task DropTableAsync(string tableName, CancellationToken ct = default)
    {
        var sql = _sqlDialect.GenerateDropTableSql(tableName);
        return ExecuteSqlAsync(sql, null, ct);
    }

    private void AttachParameters(DbCommand cmd, object? parameters)
    {
        if (parameters == null) return;
        var prefix = Dialect == DatabaseDialect.Oracle ? ":" : "@";

        if (parameters is IEnumerable<(string Key, object? Value)> tupleParams)
        {
            foreach (var (key, value) in tupleParams)
            {
                var p = cmd.CreateParameter();
                p.ParameterName = prefix + key.TrimStart('@', ':');
                p.Value = value ?? DBNull.Value;
                cmd.Parameters.Add(p);
            }
            return;
        }

        if (parameters is IEnumerable<KeyValuePair<string, object?>> kvpParams)
        {
            foreach (var kvp in kvpParams)
            {
                var p = cmd.CreateParameter();
                p.ParameterName = prefix + kvp.Key.TrimStart('@', ':');
                p.Value = kvp.Value ?? DBNull.Value;
                cmd.Parameters.Add(p);
            }
            return;
        }

        throw new NotSupportedException(
            $"[LinKit Native AOT] Passing anonymous object '{parameters.GetType().FullName}' to parameters is not supported in Native AOT mode. " +
            "Please pass Dictionary<string, object?> or IEnumerable<(string, object?)> instead."
        );
    }
}
