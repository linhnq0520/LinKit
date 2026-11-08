using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LinKit.Data;

public abstract class EntityMap<TEntity, TKey>
    where TEntity : class
{
    public abstract TableModel Model { get; }
    public abstract TEntity Read(DbDataReader reader);
    public abstract object?[] Values(TEntity entity);
    public abstract TKey Key(TEntity entity);
    public abstract void SetKey(TEntity entity, object value);
    public abstract Table<TEntity> Table(string alias);

    public virtual void SetVersion(TEntity entity, long value) { }

    public virtual void SetTenant(TEntity entity, string value) { }
}

public class Repository<TEntity, TKey>(DataSession session, EntityMap<TEntity, TKey> map)
    where TEntity : class
{
    public DataSession Session { get; } = session;
    public EntityMap<TEntity, TKey> Mapping { get; } = map;

    public Query<TEntity> Query() => Session.From(Mapping.Table("entity"));

    public Task<TEntity?> FindAsync(TKey key, CancellationToken ct = default) =>
        Query()
            .Where(new Column<TKey>("entity", Mapping.Model.Key).Equal(key))
            .SingleOrDefaultAsync(ct);

    public Task<TEntity?> GetByIdAsync(TKey id, CancellationToken ct = default) =>
        FindAsync(id, ct);

    public Task<List<TEntity>> ListAsync(
        int skip = 0,
        int take = 100,
        CancellationToken ct = default
    ) => Query().Skip(skip).Take(take).ToListAsync(ct);

    public Task<PagedResult<TEntity>> GetPagedAsync(
        int pageNumber,
        int pageSize,
        CancellationToken ct = default
    ) => Query().ToPagedListAsync(pageNumber, pageSize, ct);

    public Task<long> CountAsync(CancellationToken ct = default) => Query().CountAsync(ct);

    public Task<bool> ExistsAsync(TKey id, CancellationToken ct = default) =>
        Query().Where(new Column<TKey>("entity", Mapping.Model.Key).Equal(id)).AnyAsync(ct);

    /// <summary>
    /// Loads every entity whose key is in <paramref name="keys"/> with one chunked <c>key IN (...)</c> statement per
    /// parameter batch, instead of one round trip per key. Duplicate keys are collapsed, so no row is returned
    /// twice, and rows come back grouped by batch rather than in the order of <paramref name="keys"/>.
    /// </summary>
    public async Task<List<TEntity>> FindManyAsync(
        IEnumerable<TKey> keys,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(keys);
        var distinct = keys.Distinct().ToArray();
        if (distinct.Length == 0)
            return [];
        var key = new Column<TKey>("entity", Mapping.Model.Key);
        var chunkSize = Math.Max(1, Session.Dialect.MaxInItems);
        var results = new List<TEntity>(distinct.Length);
        foreach (var chunk in distinct.Chunk(chunkSize))
        {
            var rows = await Query().Where(key.In(chunk)).ToListAsync(ct).ConfigureAwait(false);
            results.AddRange(rows);
        }
        return results;
    }

    /// <summary>Answers whether any row matches the predicate, with a single-row probe.</summary>
    public Task<bool> AnyAsync(Predicate predicate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return Query().Where(predicate).AnyAsync(ct);
    }

    /// <summary>Counts the rows matching the predicate without rendering the entity projection.</summary>
    public Task<long> CountAsync(Predicate predicate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return Query().Where(predicate).CountAsync(ct);
    }

    /// <summary>Filtered, paged read. <paramref name="skip"/> is only emitted when it is greater than zero.</summary>
    public Task<List<TEntity>> ListAsync(
        Predicate predicate,
        int skip = 0,
        int take = 100,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfNegative(take);
        var query = Query().Where(predicate);
        if (skip > 0)
            query = query.Skip(skip);
        return query.Take(take).ToListAsync(ct);
    }

    /// <summary>
    /// Streams matching rows without materialising them, for large exports and read-through paths that must not
    /// buffer the whole result set.
    /// </summary>
    public IAsyncEnumerable<TEntity> StreamAsync(
        int skip = 0,
        int? take = null,
        CancellationToken ct = default
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        if (take is < 0)
            throw new ArgumentOutOfRangeException(nameof(take));
        var query = Query();
        if (skip > 0)
            query = query.Skip(skip);
        if (take is { } limit)
            query = query.Take(limit);
        return query.StreamAsync(ct);
    }

    /// <summary>Deletes the row identified by the entity's key.</summary>
    public Task DeleteAsync(TEntity entity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return DeleteAsync(Mapping.Key(entity), ct);
    }

    public Task<int> DeleteWhereAsync(Predicate predicate, CancellationToken ct = default) =>
        Query().Where(predicate).ExecuteDeleteAsync(ct: ct);

    private object?[] WriteValues(TEntity entity, bool insert)
    {
        var values = Mapping.Values(entity);
        for (var index = 0; index < Mapping.Model.Columns.Count; index++)
        {
            var column = Mapping.Model.Columns[index];
            if (column.Tenant)
            {
                if (string.IsNullOrWhiteSpace(Session.TenantId))
                    throw new InvalidOperationException("A tenant is required for this write.");
                if (!insert && string.IsNullOrEmpty(values[index] as string))
                    values[index] = Session.TenantId;
                if (!Equals(values[index], Session.TenantId))
                    throw new InvalidOperationException(
                        "Entity tenant does not match session tenant."
                    );
            }
            if (insert && column.Version)
                values[index] = 1L;
            if (column.SoftDelete)
            {
                var isActive =
                    column.Type == DbType.Boolean
                        ? Equals(values[index], false)
                        : values[index] is null;
                if (!isActive)
                    throw new InvalidOperationException(
                        "Normal writes require a non-deleted entity."
                    );
            }
            if (!column.Nullable && !column.Generated && values[index] is null)
                throw new ArgumentException($"Column '{column.Name}' cannot be null.");
        }
        return values;
    }

    public CommandSpec InsertCommand(TEntity entity)
    {
        var values = WriteValues(entity, true);
        var dialect = Session.Dialect;
        var writer = new SqlWriter(dialect, Session.TenantId);
        var columns = Mapping
            .Model.Columns.Select((column, index) => (column, index))
            .Where(item => !item.column.Generated)
            .ToArray();
        var table = dialect.Table(Mapping.Model);
        var key = Mapping.Model.Key;
        string sql;
        if (columns.Length == 0)
        {
            sql = dialect.Kind switch
            {
                DatabaseKind.MySql => $"INSERT INTO {table} () VALUES ()",
                DatabaseKind.Oracle =>
                    $"INSERT INTO {table} ({dialect.Quote(key.Name)}) VALUES (DEFAULT)",
                _ => $"INSERT INTO {table}"
                    + (
                        key.Generated && dialect.Kind == DatabaseKind.SqlServer
                            ? $" OUTPUT INSERTED.{dialect.Quote(key.Name)}"
                            : ""
                    )
                    + " DEFAULT VALUES",
            };
        }
        else
        {
            var names = string.Join(", ", columns.Select(item => dialect.Quote(item.column.Name)));
            var parameters = string.Join(
                ", ",
                columns.Select(item => writer.Parameter(values[item.index], item.column))
            );
            sql =
                $"INSERT INTO {table} ({names})"
                + (
                    key.Generated && dialect.Kind == DatabaseKind.SqlServer
                        ? $" OUTPUT INSERTED.{dialect.Quote(key.Name)}"
                        : ""
                )
                + $" VALUES ({parameters})";
        }
        if (key.Generated)
        {
            sql += dialect.Kind switch
            {
                DatabaseKind.Sqlite or DatabaseKind.PostgreSql =>
                    $" RETURNING {dialect.Quote(key.Name)}",
                DatabaseKind.MySql => "; SELECT LAST_INSERT_ID()",
                DatabaseKind.Oracle => $" RETURNING {dialect.Quote(key.Name)} INTO :generated_key",
                _ => "",
            };
        }
        return new(sql, writer.Arguments);
    }

    public async Task<TKey> InsertAsync(TEntity entity, CancellationToken ct = default)
    {
        var spec = InsertCommand(entity);
        if (Mapping.Model.Key.Generated)
            Mapping.SetKey(
                entity,
                await Session.InsertIdentityAsync(spec, Mapping.Model.Key, ct).ConfigureAwait(false)
            );
        else
            await Session.ExecuteAsync(spec, ct).ConfigureAwait(false);
        Mapping.SetVersion(entity, 1L);
        if (Session.TenantId is not null)
            Mapping.SetTenant(entity, Session.TenantId);
        return Mapping.Key(entity);
    }

    private string WhereKey(SqlWriter writer, TKey key, long? version)
    {
        var dialect = Session.Dialect;
        var conditions = new List<string>
        {
            dialect.Quote(Mapping.Model.Key.Name)
                + " = "
                + writer.Parameter(key, Mapping.Model.Key),
        };
        foreach (var column in Mapping.Model.Columns)
        {
            if (column.Tenant)
            {
                if (string.IsNullOrWhiteSpace(Session.TenantId))
                    throw new InvalidOperationException("A tenant is required for this write.");
                conditions.Add(
                    dialect.Quote(column.Name) + " = " + writer.Parameter(Session.TenantId, column)
                );
            }
            if (column.SoftDelete)
            {
                if (column.Type == DbType.Boolean)
                    conditions.Add(dialect.Quote(column.Name) + " = " + dialect.False);
                else
                {
                    if (!column.Nullable)
                        throw new InvalidOperationException(
                            $"Timestamp soft-delete column '{column.Name}' must be nullable."
                        );
                    conditions.Add(dialect.Quote(column.Name) + " IS NULL");
                }
            }
            if (column.Version)
            {
                if (version is null or < 1)
                    throw new ArgumentException(
                        "An original positive concurrency version is required."
                    );
                conditions.Add(
                    dialect.Quote(column.Name) + " = " + writer.Parameter(version, column)
                );
            }
        }
        return string.Join(" AND ", conditions);
    }

    public async Task UpdateAsync(TEntity entity, CancellationToken ct = default)
    {
        var values = WriteValues(entity, false);
        var writer = new SqlWriter(Session.Dialect, Session.TenantId);
        var sets = new List<string>();
        long? version = null;
        for (var index = 0; index < Mapping.Model.Columns.Count; index++)
        {
            var column = Mapping.Model.Columns[index];
            if (column.Version)
            {
                version = (long)values[index]!;
                _ = checked(version.Value + 1);
                sets.Add(
                    Session.Dialect.Quote(column.Name)
                        + " = "
                        + Session.Dialect.Quote(column.Name)
                        + " + 1"
                );
            }
            else if (
                !column.PrimaryKey
                && !column.Generated
                && !column.Tenant
                && !column.SoftDelete
            )
            {
                sets.Add(
                    Session.Dialect.Quote(column.Name)
                        + " = "
                        + writer.Parameter(values[index], column)
                );
            }
        }
        if (sets.Count == 0)
            throw new NotSupportedException("Entity has no writable columns.");
        var where = WhereKey(writer, Mapping.Key(entity), version);
        var affected = await Session
            .ExecuteAsync(
                new(
                    $"UPDATE {Session.Dialect.Table(Mapping.Model)} SET {string.Join(", ", sets)} WHERE {where}",
                    writer.Arguments
                ),
                ct
            )
            .ConfigureAwait(false);
        if (affected != 1)
            throw new DataConcurrencyException(
                "Update expected one visible row with the original version."
            );
        if (version.HasValue)
            Mapping.SetVersion(entity, checked(version.Value + 1));
    }

    public async Task DeleteAsync(TKey key, long? expectedVersion, CancellationToken ct = default)
    {
        var writer = new SqlWriter(Session.Dialect, Session.TenantId);
        var where = WhereKey(writer, key, expectedVersion);
        var soft = Mapping.Model.Columns.SingleOrDefault(column => column.SoftDelete);
        var version = Mapping.Model.Columns.SingleOrDefault(column => column.Version);
        var table = Session.Dialect.Table(Mapping.Model);
        var deletedValue =
            soft?.Type == DbType.Boolean ? Session.Dialect.True : "CURRENT_TIMESTAMP";
        var sql = soft is null
            ? $"DELETE FROM {table} WHERE {where}"
            : $"UPDATE {table} SET {Session.Dialect.Quote(soft.Name)} = {deletedValue}"
                + (
                    version is null
                        ? ""
                        : $", {Session.Dialect.Quote(version.Name)} = {Session.Dialect.Quote(version.Name)} + 1"
                )
                + $" WHERE {where}";
        if (await Session.ExecuteAsync(new(sql, writer.Arguments), ct).ConfigureAwait(false) != 1)
            throw new DataConcurrencyException(
                "Delete expected one visible row with the original version."
            );
    }

    public Task DeleteAsync(TKey key, CancellationToken ct = default) => DeleteAsync(key, null, ct);

    private static IList<T> Materialize<T>(IEnumerable<T> source) =>
        source as IList<T> ?? source.ToList();

    /// <summary>
    /// Rows that still fit into one statement once <paramref name="fixedParameters"/> non-row parameters are
    /// reserved. Never returns zero, so a single over-wide row still produces one (possibly rejected) statement.
    /// </summary>
    private int RowsPerStatement(int parametersPerRow, int requested, int fixedParameters = 0)
    {
        if (parametersPerRow <= 0)
            return 1;
        var available = Math.Max(1, Session.Dialect.MaxParameters - fixedParameters);
        var byParameters = available / parametersPerRow;
        if (byParameters < 1)
            byParameters = 1;
        return Math.Min(Math.Min(requested, byParameters), SqlDialect.MaxRowsPerInsertStatement);
    }

    /// <summary>Active-row predicate for a soft-delete column, optionally qualified with a table alias.</summary>
    private string ActivePredicate(string? alias, ColumnModel column)
    {
        var name =
            (alias is null ? string.Empty : alias + ".") + Session.Dialect.Quote(column.Name);
        if (column.Type == DbType.Boolean)
            return name + " = " + Session.Dialect.False;
        if (!column.Nullable)
            throw new InvalidOperationException(
                $"Timestamp soft-delete column '{column.Name}' must be nullable."
            );
        return name + " IS NULL";
    }

    public Task InsertRangeAsync(IEnumerable<TEntity> entities, CancellationToken ct = default) =>
        InsertRangeAsync(entities, Session.BatchSize, ct);

    /// <summary>
    /// Inserts all entities using multi-row <c>INSERT INTO t (...) VALUES (...), (...)</c> statements, chunked by the
    /// provider parameter limit and the requested batch size. Generated keys are read back with
    /// <c>OUTPUT INSERTED</c> (SQL Server) or <c>RETURNING</c> (PostgreSQL, SQLite) and assigned to the entities in
    /// insertion order. The per-row loop is kept as the documented fallback for providers without multi-row insert
    /// support (Oracle), providers that cannot return a batch of generated keys (MySQL), and keyless tables.
    /// </summary>
    public async Task InsertRangeAsync(
        IEnumerable<TEntity> entities,
        int batchSize,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        var items = Materialize(entities);
        if (items.Count == 0)
            return;

        var columns = Mapping
            .Model.Columns.Select((column, index) => (column, index))
            .Where(item => !item.column.Generated)
            .ToArray();
        var identity = Mapping.Model.Key.Generated;
        var batched =
            columns.Length > 0
            && Session.Dialect.SupportsMultiRowInsert
            && (!identity || Session.Dialect.SupportsBatchInsertIdentity);

        await using var transaction = Session.HasTransaction
            ? null
            : await Session.BeginTransactionAsync(ct: ct).ConfigureAwait(false);
        if (!batched)
        {
            foreach (var entity in items)
                await InsertAsync(entity, ct).ConfigureAwait(false);
        }
        else
        {
            var rows = RowsPerStatement(columns.Length, batchSize);
            List<object>? generated = identity ? [] : null;
            foreach (var chunk in items.Chunk(rows))
            {
                var spec = InsertRangeCommand(chunk, columns);
                if (generated is null)
                {
                    await Session.ExecuteAsync(spec, ct).ConfigureAwait(false);
                    continue;
                }
                var keys = await Session.ScalarListAsync(spec, ct).ConfigureAwait(false);
                if (keys.Count != chunk.Length)
                {
                    throw new DataMappingException(
                        $"INSERT returned {keys.Count} generated keys for {chunk.Length} inserted rows."
                    );
                }
                generated.AddRange(keys);
            }
            if (generated is not null)
            {
                for (var index = 0; index < items.Count; index++)
                    Mapping.SetKey(items[index], generated[index]);
            }
            foreach (var entity in items)
            {
                Mapping.SetVersion(entity, 1L);
                if (Session.TenantId is not null)
                    Mapping.SetTenant(entity, Session.TenantId);
            }
        }
        if (transaction is not null)
            await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    private CommandSpec InsertRangeCommand(
        IReadOnlyList<TEntity> entities,
        (ColumnModel column, int index)[] columns
    )
    {
        var dialect = Session.Dialect;
        var writer = new SqlWriter(dialect, Session.TenantId);
        var key = Mapping.Model.Key;
        var names = string.Join(", ", columns.Select(item => dialect.Quote(item.column.Name)));
        var rows = new string[entities.Count];
        for (var row = 0; row < entities.Count; row++)
        {
            var values = WriteValues(entities[row], true);
            rows[row] =
                "("
                + string.Join(
                    ", ",
                    columns.Select(item => writer.Parameter(values[item.index], item.column))
                )
                + ")";
        }
        var sql =
            $"INSERT INTO {dialect.Table(Mapping.Model)} ({names})"
            + (
                key.Generated && dialect.Kind == DatabaseKind.SqlServer
                    ? $" OUTPUT INSERTED.{dialect.Quote(key.Name)}"
                    : string.Empty
            )
            + " VALUES "
            + string.Join(", ", rows);
        if (key.Generated && dialect.Kind is DatabaseKind.Sqlite or DatabaseKind.PostgreSql)
            sql += $" RETURNING {dialect.Quote(key.Name)}";
        return new(sql, writer.Arguments);
    }

    public Task UpdateRangeAsync(IEnumerable<TEntity> entities, CancellationToken ct = default) =>
        UpdateRangeAsync(entities, Session.BatchSize, ct);

    /// <summary>
    /// Updates all entities with one statement per batch using a values table in the join, instead of one statement
    /// per entity. PostgreSQL and SQL Server use <c>FROM (VALUES ...) AS v(...)</c>; MySQL uses
    /// <c>INNER JOIN (SELECT ... UNION ALL ...)</c>. The per-row loop is kept as the documented fallback for SQLite
    /// and Oracle. Optimistic concurrency is preserved by matching the original version of every row.
    /// </summary>
    public async Task UpdateRangeAsync(
        IEnumerable<TEntity> entities,
        int batchSize,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        var items = Materialize(entities);
        if (items.Count == 0)
            return;

        var setColumns = Mapping
            .Model.Columns.Select((column, index) => (column, index))
            .Where(item =>
                !item.column.PrimaryKey
                && !item.column.Generated
                && !item.column.Tenant
                && !item.column.SoftDelete
                && !item.column.Version
            )
            .ToArray();
        if (setColumns.Length == 0)
            throw new NotSupportedException("Entity has no writable columns.");

        var dialect = Session.Dialect;
        var version = Mapping.Model.Columns.SingleOrDefault(column => column.Version);
        var versionIndex = -1;
        for (var index = 0; index < Mapping.Model.Columns.Count; index++)
        {
            if (version is not null && ReferenceEquals(Mapping.Model.Columns[index], version))
                versionIndex = index;
        }
        var batched = dialect.SupportsUpdateFromValues || dialect.SupportsUpdateJoinValues;

        await using var transaction = Session.HasTransaction
            ? null
            : await Session.BeginTransactionAsync(ct: ct).ConfigureAwait(false);
        if (!batched)
        {
            foreach (var entity in items)
                await UpdateAsync(entity, ct).ConfigureAwait(false);
        }
        else
        {
            var tenantCount = Mapping.Model.Columns.Count(column => column.Tenant);
            var perRow = setColumns.Length + 1 + (version is null ? 0 : 1);
            var rows = RowsPerStatement(perRow, batchSize, tenantCount);
            // MySQL reports matched rows only when they actually change; the version bump guarantees a change
            // whenever a concurrency column exists, so the affected-row assertion is limited to that case there.
            var assertAffected = version is not null || dialect.Kind != DatabaseKind.MySql;
            var versions = version is null ? null : new List<long>(items.Count);
            foreach (var chunk in items.Chunk(rows))
            {
                var spec = UpdateRangeCommand(chunk, setColumns, version, versionIndex, versions);
                var affected = await Session.ExecuteAsync(spec, ct).ConfigureAwait(false);
                if (assertAffected && affected != chunk.Length)
                {
                    throw new DataConcurrencyException(
                        "Update expected one visible row with the original version per entity."
                    );
                }
            }
            if (version is not null && versions is not null)
            {
                for (var index = 0; index < items.Count; index++)
                    Mapping.SetVersion(items[index], checked(versions[index] + 1));
            }
        }
        if (transaction is not null)
            await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    private CommandSpec UpdateRangeCommand(
        IReadOnlyList<TEntity> entities,
        (ColumnModel column, int index)[] setColumns,
        ColumnModel? version,
        int versionIndex,
        List<long>? originalVersions
    )
    {
        var dialect = Session.Dialect;
        var writer = new SqlWriter(dialect, Session.TenantId);
        var key = Mapping.Model.Key;
        // Value columns of the row set: 0 = primary key, 1 = original version (when present), then the SET payload.
        var payloadOffset = version is null ? 1 : 2;
        var valueCount = payloadOffset + setColumns.Length;
        var valueNames = new string[valueCount];
        for (var index = 0; index < valueCount; index++)
            valueNames[index] = "v" + index.ToString(CultureInfo.InvariantCulture);

        var rows = new string[entities.Count];
        for (var row = 0; row < entities.Count; row++)
        {
            var entity = entities[row];
            var values = WriteValues(entity, false);
            var cells = new string[valueCount];
            var cell = 0;
            cells[cell++] = writer.Parameter(Mapping.Key(entity), key);
            if (version is not null)
            {
                var original = Convert.ToInt64(values[versionIndex], CultureInfo.InvariantCulture);
                if (original < 1)
                    throw new ArgumentException(
                        "An original positive concurrency version is required."
                    );
                _ = checked(original + 1);
                originalVersions?.Add(original);
                cells[cell++] = writer.Parameter(original, version);
            }
            foreach (var item in setColumns)
                cells[cell++] = writer.Parameter(values[item.index], item.column);
            rows[row] = "(" + string.Join(", ", cells) + ")";
        }

        // PostgreSQL and SQL Server reject qualified names on the left side of SET; MySQL requires them.
        string Target(ColumnModel column) =>
            dialect.Kind == DatabaseKind.MySql
                ? "t." + dialect.Quote(column.Name)
                : dialect.Quote(column.Name);

        var sets = new List<string>(setColumns.Length + 1);
        for (var index = 0; index < setColumns.Length; index++)
            sets.Add(Target(setColumns[index].column) + " = " + valueNames[payloadOffset + index]);
        if (version is not null)
            sets.Add(Target(version) + " = " + Target(version) + " + 1");

        var joins = new List<string> { "t." + dialect.Quote(key.Name) + " = " + valueNames[0] };
        if (version is not null)
            joins.Add("t." + dialect.Quote(version.Name) + " = " + valueNames[1]);
        foreach (var column in Mapping.Model.Columns)
        {
            if (column.Tenant)
            {
                if (string.IsNullOrWhiteSpace(Session.TenantId))
                    throw new InvalidOperationException("A tenant is required for this write.");
                joins.Add(
                    "t."
                        + dialect.Quote(column.Name)
                        + " = "
                        + writer.Parameter(Session.TenantId, column)
                );
            }
            if (column.SoftDelete)
                joins.Add(ActivePredicate("t", column));
        }

        var table = dialect.Table(Mapping.Model);
        string sql;
        if (dialect.SupportsUpdateJoinValues)
        {
            // MySQL: the row set must be a derived table, and SET follows the join.
            var selects = new string[entities.Count];
            for (var row = 0; row < entities.Count; row++)
            {
                var cells = rows[row].Substring(1, rows[row].Length - 2);
                selects[row] =
                    row == 0 ? "SELECT " + AliasCells(cells, valueNames) : "SELECT " + cells;
            }
            sql =
                $"UPDATE {table} AS t INNER JOIN ({string.Join(" UNION ALL ", selects)}) AS v({string.Join(", ", valueNames)}) ON {joins[0]}"
                + $" SET {string.Join(", ", sets)}"
                + (
                    joins.Count > 1 ? " WHERE " + string.Join(" AND ", joins.Skip(1)) : string.Empty
                );
        }
        else if (dialect.Kind == DatabaseKind.SqlServer)
        {
            sql =
                $"UPDATE t SET {string.Join(", ", sets)} FROM {table} AS t INNER JOIN (VALUES {string.Join(", ", rows)}) AS v({string.Join(", ", valueNames)}) ON {joins[0]}"
                + (
                    joins.Count > 1 ? " WHERE " + string.Join(" AND ", joins.Skip(1)) : string.Empty
                );
        }
        else
        {
            sql =
                $"UPDATE {table} AS t SET {string.Join(", ", sets)} FROM (VALUES {string.Join(", ", rows)}) AS v({string.Join(", ", valueNames)})"
                + $" WHERE {string.Join(" AND ", joins)}";
        }
        return new(sql, writer.Arguments);
    }

    private static string AliasCells(string cells, string[] names)
    {
        var parts = cells.Split(", ");
        for (var index = 0; index < parts.Length && index < names.Length; index++)
            parts[index] += " AS " + names[index];
        return string.Join(", ", parts);
    }

    public Task DeleteRangeAsync(IEnumerable<TKey> ids, CancellationToken ct = default) =>
        DeleteRangeAsync(ids, Session.BatchSize, ct);

    /// <summary>
    /// Deletes all keys with one chunked <c>key IN (...)</c> statement per batch (soft-delete entities become a
    /// single <c>UPDATE ... WHERE key IN (...)</c>). Tenant and active-row predicates are applied to every statement.
    /// </summary>
    public async Task DeleteRangeAsync(
        IEnumerable<TKey> ids,
        int batchSize,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        var keys = Materialize(ids);
        if (keys.Count == 0)
            return;

        var dialect = Session.Dialect;
        var key = Mapping.Model.Key;
        var soft = Mapping.Model.Columns.SingleOrDefault(column => column.SoftDelete);
        var version = Mapping.Model.Columns.SingleOrDefault(column => column.Version);
        var table = dialect.Table(Mapping.Model);
        var deletedValue = soft?.Type == DbType.Boolean ? dialect.True : "CURRENT_TIMESTAMP";
        var tenantCount = Mapping.Model.Columns.Count(column => column.Tenant);
        var limit = RowsPerStatement(1, batchSize, tenantCount);

        await using var transaction = Session.HasTransaction
            ? null
            : await Session.BeginTransactionAsync(ct: ct).ConfigureAwait(false);
        var affectedTotal = 0;
        foreach (var chunk in keys.Chunk(limit))
        {
            var writer = new SqlWriter(dialect, Session.TenantId);
            var inList = string.Join(", ", chunk.Select(id => writer.Parameter(id, key)));
            var conditions = new List<string> { dialect.Quote(key.Name) + " IN (" + inList + ")" };
            foreach (var column in Mapping.Model.Columns)
            {
                if (column.Tenant)
                {
                    if (string.IsNullOrWhiteSpace(Session.TenantId))
                        throw new InvalidOperationException("A tenant is required for this write.");
                    conditions.Add(
                        dialect.Quote(column.Name)
                            + " = "
                            + writer.Parameter(Session.TenantId, column)
                    );
                }
                if (column.SoftDelete)
                    conditions.Add(ActivePredicate(null, column));
            }
            var where = string.Join(" AND ", conditions);
            var sql = soft is null
                ? $"DELETE FROM {table} WHERE {where}"
                : $"UPDATE {table} SET {dialect.Quote(soft.Name)} = {deletedValue}"
                    + (
                        version is null
                            ? string.Empty
                            : $", {dialect.Quote(version.Name)} = {dialect.Quote(version.Name)} + 1"
                    )
                    + $" WHERE {where}";
            affectedTotal += await Session
                .ExecuteAsync(new(sql, writer.Arguments), ct)
                .ConfigureAwait(false);
        }
        // MySQL reports matched rows only when they change; the version bump makes a soft delete always change a row.
        if (soft is null || version is not null || dialect.Kind != DatabaseKind.MySql)
        {
            if (affectedTotal != keys.Count)
            {
                throw new DataConcurrencyException(
                    "Delete expected every selected row to be visible with the original version."
                );
            }
        }
        if (transaction is not null)
            await transaction.CommitAsync(ct).ConfigureAwait(false);
    }
}
