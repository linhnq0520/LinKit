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
    /// <summary>Applies every migration that is not recorded in the history table yet.</summary>
    /// <param name="ct">Token used to cancel the run; a migration that already started is rolled back.</param>
    /// <returns>A task that completes when no pending migration is left.</returns>
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

    /// <summary>Creates the runner.</summary>
    /// <param name="connectionFactory">Factory used to open the migration connection.</param>
    /// <param name="registry">Registry produced by the source generator.</param>
    /// <param name="serviceProvider">Provider used to construct each migration instance.</param>
    /// <param name="logger">Optional logger that records the progress of every migration.</param>
    public MigrationRunner(
        IDbConnectionFactory connectionFactory,
        IMigrationRegistry registry,
        IServiceProvider serviceProvider,
        ILogger<MigrationRunner>? logger = null
    )
    {
        _connectionFactory = connectionFactory;
        _registry = registry;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">The distributed migration lock could not be acquired.</exception>
    /// <exception cref="Exception">A migration failed; its transaction is rolled back and the failure is rethrown.</exception>
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

        await AcquireMigrationLockAsync(conn, _connectionFactory.Dialect, ct);
        try
        {
            await RunPendingMigrationsAsync(conn, dialect, migrations, ct);
        }
        finally
        {
            await ReleaseMigrationLockAsync(conn, _connectionFactory.Dialect, ct);
        }
    }

    private async Task RunPendingMigrationsAsync(
        DbConnection conn,
        ISqlDialect dialect,
        IReadOnlyList<MigrationDescriptor> migrations,
        CancellationToken ct
    )
    {
        // 1. Ensure __linkit_migrations history table exists
        var createTableSql = dialect.GenerateCreateMigrationsTableSql();
        await ExecuteNonQueryAsync(conn, createTableSql, null, ct);

        // 2. Query applied migrations
        var appliedIds = await GetAppliedMigrationIdsAsync(conn, dialect, ct);

        // 3. Filter pending migrations in natural version order (numeric runs compared as numbers)
        var pending = migrations
            .Where(m => !appliedIds.Contains(m.Version))
            .OrderBy(m => m.Version, MigrationVersionComparer.Instance)
            .ToList();

        if (pending.Count == 0)
        {
            _logger?.LogInformation(
                "LinKit.Data: All {Count} migrations are already up-to-date.",
                migrations.Count
            );
            return;
        }

        _logger?.LogInformation(
            "LinKit.Data: Applying {Count} pending data migrations...",
            pending.Count
        );

        // 4. Apply each migration in an isolated transaction
        foreach (var desc in pending)
        {
            _logger?.LogInformation(
                "Applying migration [{Version}] {Description}...",
                desc.Version,
                desc.Description
            );
            var sw = Stopwatch.StartNew();

            await using var tx = await conn.BeginTransactionAsync(ct);
            try
            {
                var migrationInstance = desc.Factory(_serviceProvider);
                var context = new MigrationContext(
                    conn,
                    tx,
                    _connectionFactory.Dialect,
                    _serviceProvider
                );

                await migrationInstance.UpAsync(context, ct);

                sw.Stop();

                // Record in history table
                await RecordMigrationAppliedAsync(
                    conn,
                    tx,
                    dialect,
                    desc.Version,
                    desc.Description,
                    sw.ElapsedMilliseconds,
                    ct
                );

                try
                {
                    await tx.CommitAsync(ct);
                }
                catch (InvalidOperationException)
                    when (_connectionFactory.Dialect
                            is DatabaseDialect.MySql
                                or DatabaseDialect.Oracle
                    )
                {
                    // In MySQL and Oracle, DDL statements cause an automatic implicit transaction commit
                }

                _logger?.LogInformation(
                    "Migration [{Version}] applied successfully in {Elapsed}ms.",
                    desc.Version,
                    sw.ElapsedMilliseconds
                );
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

    private static async Task<HashSet<string>> GetAppliedMigrationIdsAsync(
        DbConnection conn,
        ISqlDialect dialect,
        CancellationToken ct
    )
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sql = dialect.DialectType switch
        {
            DatabaseDialect.SqlServer => "SELECT [MigrationId] FROM [__linkit_migrations]",
            DatabaseDialect.MySql => "SELECT `MigrationId` FROM `__linkit_migrations`",
            _ => "SELECT \"MigrationId\" FROM \"__linkit_migrations\"",
        };

        // The history table was created above; a failure here is a real error and must not be
        // swallowed, otherwise already-applied migrations would be re-executed.
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            set.Add(reader.GetString(0));
        }

        return set;
    }

    // Fixed key so every instance contends on the same lock.
    private const string MigrationLockResource = "__linkit_migrations";
    private const int PostgresMigrationLockKey = 760311;

    private static async Task AcquireMigrationLockAsync(
        DbConnection conn,
        DatabaseDialect dialect,
        CancellationToken ct
    )
    {
        switch (dialect)
        {
            case DatabaseDialect.PostgreSql:
                await ExecuteScalarAsync(
                    conn,
                    $"SELECT pg_advisory_lock({PostgresMigrationLockKey})",
                    ct
                );
                break;
            case DatabaseDialect.SqlServer:
            {
                var result = await ExecuteScalarAsync(
                    conn,
                    "DECLARE @r int; EXEC @r = sp_getapplock @Resource = N'"
                        + MigrationLockResource
                        + "', @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 60000; SELECT @r;",
                    ct
                );
                if (result is null || Convert.ToInt32(result) < 0)
                    throw new InvalidOperationException(
                        "Could not acquire the LinKit migration lock (sp_getapplock). Another instance may be running migrations."
                    );
                break;
            }
            case DatabaseDialect.MySql:
            {
                var result = await ExecuteScalarAsync(
                    conn,
                    $"SELECT GET_LOCK('{MigrationLockResource}', 60)",
                    ct
                );
                if (result is null || Convert.ToInt32(result) != 1)
                    throw new InvalidOperationException(
                        "Could not acquire the LinKit migration lock (GET_LOCK). Another instance may be running migrations."
                    );
                break;
            }
            default:
                // SQLite serializes writers at the database level; Oracle has no lightweight
                // session advisory lock here. Concurrent startups on these providers are not locked.
                break;
        }
    }

    private static async Task ReleaseMigrationLockAsync(
        DbConnection conn,
        DatabaseDialect dialect,
        CancellationToken ct
    )
    {
        try
        {
            switch (dialect)
            {
                case DatabaseDialect.PostgreSql:
                    await ExecuteScalarAsync(
                        conn,
                        $"SELECT pg_advisory_unlock({PostgresMigrationLockKey})",
                        ct
                    );
                    break;
                case DatabaseDialect.SqlServer:
                    await ExecuteScalarAsync(
                        conn,
                        "EXEC sp_releaseapplock @Resource = N'"
                            + MigrationLockResource
                            + "', @LockOwner = 'Session';",
                        ct
                    );
                    break;
                case DatabaseDialect.MySql:
                    await ExecuteScalarAsync(
                        conn,
                        $"SELECT RELEASE_LOCK('{MigrationLockResource}')",
                        ct
                    );
                    break;
            }
        }
        catch (Exception ex)
        {
            // Never mask the original migration outcome; the lock is session-scoped and dies with the connection.
            Debug.WriteLine($"LinKit.Data: Failed to release migration lock: {ex.Message}");
        }
    }

    private static async Task<object?> ExecuteScalarAsync(
        DbConnection conn,
        string sql,
        CancellationToken ct
    )
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync(ct);
    }

    private static async Task RecordMigrationAppliedAsync(
        DbConnection conn,
        DbTransaction tx,
        ISqlDialect dialect,
        string version,
        string description,
        long elapsedMs,
        CancellationToken ct
    )
    {
        var prefix = dialect.DialectType == DatabaseDialect.Oracle ? ":" : "@";
        var sql = dialect.DialectType switch
        {
            DatabaseDialect.SqlServer =>
                $"INSERT INTO [__linkit_migrations] ([MigrationId], [Description], [AppliedAt], [ExecutionTimeMs]) VALUES ({prefix}id, {prefix}desc, {prefix}applied, {prefix}time)",
            DatabaseDialect.MySql =>
                $"INSERT INTO `__linkit_migrations` (`MigrationId`, `Description`, `AppliedAt`, `ExecutionTimeMs`) VALUES ({prefix}id, {prefix}desc, {prefix}applied, {prefix}time)",
            _ =>
                $"INSERT INTO \"__linkit_migrations\" (\"MigrationId\", \"Description\", \"AppliedAt\", \"ExecutionTimeMs\") VALUES ({prefix}id, {prefix}desc, {prefix}applied, {prefix}time)",
        };

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;

        var pId = cmd.CreateParameter();
        pId.ParameterName = $"{prefix}id";
        pId.Value = version;
        cmd.Parameters.Add(pId);
        var pDesc = cmd.CreateParameter();
        pDesc.ParameterName = $"{prefix}desc";
        pDesc.Value = description ?? "";
        cmd.Parameters.Add(pDesc);
        var pApplied = cmd.CreateParameter();
        pApplied.ParameterName = $"{prefix}applied";
        pApplied.Value = DateTimeOffset.UtcNow;
        cmd.Parameters.Add(pApplied);
        var pTime = cmd.CreateParameter();
        pTime.ParameterName = $"{prefix}time";
        pTime.Value = elapsedMs;
        cmd.Parameters.Add(pTime);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task ExecuteNonQueryAsync(
        DbConnection conn,
        string sql,
        DbTransaction? tx,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(sql))
            return;

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>
/// Natural-order comparison so numeric version segments sort numerically ("v9" before "v10").
/// </summary>
internal sealed class MigrationVersionComparer : IComparer<string>
{
    public static readonly MigrationVersionComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
            return 0;
        if (x is null)
            return -1;
        if (y is null)
            return 1;

        int i = 0,
            j = 0;
        while (i < x.Length && j < y.Length)
        {
            char cx = x[i],
                cy = y[j];
            if (char.IsDigit(cx) && char.IsDigit(cy))
            {
                int si = i,
                    sj = j;
                while (i < x.Length && char.IsDigit(x[i]))
                    i++;
                while (j < y.Length && char.IsDigit(y[j]))
                    j++;

                // Skip leading zeros, then compare by length and digits to avoid overflow.
                ReadOnlySpan<char> nx = x.AsSpan(si, i - si).TrimStart('0');
                ReadOnlySpan<char> ny = y.AsSpan(sj, j - sj).TrimStart('0');
                if (nx.Length != ny.Length)
                    return nx.Length - ny.Length;
                int cmp = nx.CompareTo(ny, StringComparison.Ordinal);
                if (cmp != 0)
                    return cmp;
            }
            else
            {
                if (cx != cy)
                    return cx.CompareTo(cy);
                i++;
                j++;
            }
        }

        return (x.Length - i) - (y.Length - j);
    }
}
