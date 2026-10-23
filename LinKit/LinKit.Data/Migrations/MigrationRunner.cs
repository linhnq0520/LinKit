using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LinKit.Data.Dialects;
using Microsoft.Extensions.Logging;

namespace LinKit.Data.Migrations;

/// <summary>
/// Service responsible for discovering and running pending data migrations at application startup.
/// </summary>
public interface IMigrationRunner
{
    Task RunMigrationsAsync(CancellationToken ct = default);
}

/// <summary>
/// Default migration runner executing pending migrations idempotently in exact version order.
/// </summary>
public sealed class MigrationRunner : IMigrationRunner
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly IMigrationRegistry _registry;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<MigrationRunner>? _logger;

    public MigrationRunner(
        IDbConnectionFactory connectionFactory,
        IMigrationRegistry registry,
        IServiceProvider serviceProvider,
        ILogger<MigrationRunner>? logger = null)
    {
        _connectionFactory = connectionFactory;
        _registry = registry;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task RunMigrationsAsync(CancellationToken ct = default)
    {
        var migrations = _registry.GetMigrations();
        if (migrations.Count == 0)
        {
            return;
        }

        var dialect = SqlDialectFactory.GetDialect(_connectionFactory.Dialect);
        await using var conn = await _connectionFactory.CreateConnectionAsync(ct);
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync(ct);
        }

        // 1. Ensure __linkit_migrations history table exists
        var createTableSql = dialect.GenerateCreateMigrationsTableSql();
        await ExecuteNonQueryAsync(conn, createTableSql, null, ct);

        // 2. Query applied migrations
        var appliedIds = await GetAppliedMigrationIdsAsync(conn, dialect, ct);

        // 3. Filter pending migrations in exact ordinal order
        var pending = migrations
            .Where(m => !appliedIds.Contains(m.Version))
            .OrderBy(m => m.Version, StringComparer.Ordinal)
            .ToList();

        if (pending.Count == 0)
        {
            _logger?.LogInformation("LinKit.Data: All {Count} migrations are already up-to-date.", migrations.Count);
            return;
        }

        _logger?.LogInformation("LinKit.Data: Applying {Count} pending data migrations...", pending.Count);

        // 4. Apply each migration in an isolated transaction
        foreach (var desc in pending)
        {
            _logger?.LogInformation("Applying migration [{Version}] {Description}...", desc.Version, desc.Description);
            var sw = Stopwatch.StartNew();

            await using var tx = await conn.BeginTransactionAsync(ct);
            try
            {
                var migrationInstance = desc.Factory(_serviceProvider);
                var context = new MigrationContext(conn, tx, _connectionFactory.Dialect, _serviceProvider);

                await migrationInstance.UpAsync(context, ct);

                sw.Stop();

                // Record in history table
                await RecordMigrationAppliedAsync(conn, tx, dialect, desc.Version, desc.Description, sw.ElapsedMilliseconds, ct);

                try
                {
                    await tx.CommitAsync(ct);
                }
                catch (InvalidOperationException) when (_connectionFactory.Dialect is DatabaseDialect.MySql or DatabaseDialect.Oracle)
                {
                    // In MySQL and Oracle, DDL statements cause an automatic implicit transaction commit
                }

                _logger?.LogInformation("Migration [{Version}] applied successfully in {Elapsed}ms.", desc.Version, sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                try
                {
                    await tx.RollbackAsync(ct);
                }
                catch
                {
                    // Ignore rollback failure if DDL already caused an implicit commit or connection was severed
                }

                _logger?.LogError(ex, "Failed to apply migration [{Version}].", desc.Version);
                throw;
            }
        }

        _logger?.LogInformation("LinKit.Data: All pending migrations completed successfully.");
    }

    private static async Task<HashSet<string>> GetAppliedMigrationIdsAsync(DbConnection conn, ISqlDialect dialect, CancellationToken ct)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sql = dialect.DialectType switch
        {
            DatabaseDialect.SqlServer => "SELECT [MigrationId] FROM [__linkit_migrations]",
            DatabaseDialect.MySql => "SELECT `MigrationId` FROM `__linkit_migrations`",
            _ => "SELECT \"MigrationId\" FROM \"__linkit_migrations\""
        };

        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                set.Add(reader.GetString(0));
            }
        }
        catch
        {
            // Table was just created or empty
        }

        return set;
    }

    private static async Task RecordMigrationAppliedAsync(
        DbConnection conn,
        DbTransaction tx,
        ISqlDialect dialect,
        string version,
        string description,
        long elapsedMs,
        CancellationToken ct)
    {
        var prefix = dialect.DialectType == DatabaseDialect.Oracle ? ":" : "@";
        var sql = dialect.DialectType switch
        {
            DatabaseDialect.SqlServer =>
                $"INSERT INTO [__linkit_migrations] ([MigrationId], [Description], [AppliedAt], [ExecutionTimeMs]) VALUES ({prefix}id, {prefix}desc, {prefix}applied, {prefix}time)",
            DatabaseDialect.MySql =>
                $"INSERT INTO `__linkit_migrations` (`MigrationId`, `Description`, `AppliedAt`, `ExecutionTimeMs`) VALUES ({prefix}id, {prefix}desc, {prefix}applied, {prefix}time)",
            _ =>
                $"INSERT INTO \"__linkit_migrations\" (\"MigrationId\", \"Description\", \"AppliedAt\", \"ExecutionTimeMs\") VALUES ({prefix}id, {prefix}desc, {prefix}applied, {prefix}time)"
        };

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;

        var pId = cmd.CreateParameter(); pId.ParameterName = $"{prefix}id"; pId.Value = version; cmd.Parameters.Add(pId);
        var pDesc = cmd.CreateParameter(); pDesc.ParameterName = $"{prefix}desc"; pDesc.Value = description ?? ""; cmd.Parameters.Add(pDesc);
        var pApplied = cmd.CreateParameter(); pApplied.ParameterName = $"{prefix}applied"; pApplied.Value = DateTimeOffset.UtcNow; cmd.Parameters.Add(pApplied);
        var pTime = cmd.CreateParameter(); pTime.ParameterName = $"{prefix}time"; pTime.Value = elapsedMs; cmd.Parameters.Add(pTime);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task ExecuteNonQueryAsync(DbConnection conn, string sql, DbTransaction? tx, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sql)) return;

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
