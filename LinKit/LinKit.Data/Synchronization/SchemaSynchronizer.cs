using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LinKit.Data.Dialects;
using Microsoft.Extensions.Logging;

namespace LinKit.Data.Synchronization;

public interface ISchemaSynchronizer
{
    Task SynchronizeAsync(CancellationToken ct = default);
}

public class SchemaSynchronizer : ISchemaSynchronizer
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly IEnumerable<ITableSchemaProvider> _schemaProviders;
    private readonly ILogger<SchemaSynchronizer>? _logger;

    public SchemaSynchronizer(
        IDbConnectionFactory connectionFactory,
        IEnumerable<ITableSchemaProvider> schemaProviders,
        ILogger<SchemaSynchronizer>? logger = null)
    {
        _connectionFactory = connectionFactory;
        _schemaProviders = schemaProviders;
        _logger = logger;
    }

    public async Task SynchronizeAsync(CancellationToken ct = default)
    {
        var dialect = SqlDialectFactory.GetDialect(_connectionFactory.Dialect);
        var tables = _schemaProviders
            .SelectMany(p => p.GetTables())
            .GroupBy(t => t.TableName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())
            .ToList();

        if (tables.Count == 0)
        {
            return;
        }

        await using var conn = await _connectionFactory.CreateConnectionAsync(ct);
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync(ct);
        }

        _logger?.LogInformation("Starting LinKit.Data Auto-Schema Synchronization ({Dialect}, {Count} entities)...",
            _connectionFactory.Dialect, tables.Count);

        foreach (var table in tables)
        {
            try
            {
                // 1. Ensure table exists
                var createTableSql = dialect.GenerateCreateTableSql(table);
                await ExecuteNonQueryAsync(conn, createTableSql, ct);

                // 2. Query existing columns and indexes in table
                var existingColumns = await GetExistingColumnNamesAsync(conn, dialect, table, ct);
                var existingIndexes = await GetExistingIndexNamesAsync(conn, dialect, table, ct);

                // 3. Detect and add missing columns
                foreach (var col in table.Columns)
                {
                    if (!existingColumns.Contains(col.ColumnName))
                    {
                        var addColSql = dialect.GenerateAddColumnSql(table, col);
                        _logger?.LogInformation("Adding column '{Column}' to table '{Table}'...", col.ColumnName, table.TableName);
                        await ExecuteNonQueryAsync(conn, addColSql, ct);
                        existingColumns.Add(col.ColumnName);
                    }

                    // 4. Create index if marked
                    if (col.IsIndexed)
                    {
                        var idxName = col.IndexName ?? $"idx_{table.TableName}_{col.PropertyName.ToLowerInvariant()}";
                        if (!existingIndexes.Contains(idxName))
                        {
                            try
                            {
                                var createIdxSql = dialect.GenerateCreateIndexSql(table, col);
                                await ExecuteNonQueryAsync(conn, createIdxSql, ct);
                                existingIndexes.Add(idxName);
                            }
                            catch (Exception ex)
                            {
                                _logger?.LogDebug(ex, "Index '{Index}' creation skipped or already managed by database.", idxName);
                            }
                        }
                    }
                }

                // 5. Create composite indexes
                foreach (var idx in table.Indexes)
                {
                    if (!existingIndexes.Contains(idx.IndexName))
                    {
                        try
                        {
                            var createIdxSql = dialect.GenerateCreateCompositeIndexSql(table, idx);
                            await ExecuteNonQueryAsync(conn, createIdxSql, ct);
                            existingIndexes.Add(idx.IndexName);
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogDebug(ex, "Composite index '{Index}' creation skipped.", idx.IndexName);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to synchronize schema for table '{Table}'", table.TableName);
                throw;
            }
        }

        _logger?.LogInformation("LinKit.Data Auto-Schema Synchronization completed successfully.");
    }

    private static async Task<HashSet<string>> GetExistingColumnNamesAsync(
        DbConnection conn,
        ISqlDialect dialect,
        TableSchemaMetadata table,
        CancellationToken ct)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sql = dialect.GenerateGetExistingColumnsSql(table);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;

        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                // SQLite PRAGMA table_info returns column name at index 1 ('name')
                // Standard information_schema returns at index 0 ('column_name')
                int idx = dialect.DialectType == DatabaseDialect.Sqlite ? 1 : 0;
                var val = reader.GetValue(idx);
                if (val != null && val != DBNull.Value)
                {
                    set.Add(val.ToString()!);
                }
            }
        }
        catch
        {
            // If table was just created or querying info schema fails, ignore
        }

        return set;
    }

    private static async Task<HashSet<string>> GetExistingIndexNamesAsync(
        DbConnection conn,
        ISqlDialect dialect,
        TableSchemaMetadata table,
        CancellationToken ct)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sql = dialect.GenerateGetExistingIndexesSql(table);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;

        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var val = reader.GetValue(0);
                if (val != null && val != DBNull.Value)
                {
                    set.Add(val.ToString()!);
                }
            }
        }
        catch
        {
            // If querying index catalog fails, ignore
        }

        return set;
    }

    private static async Task ExecuteNonQueryAsync(DbConnection conn, string sql, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sql)) return;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
