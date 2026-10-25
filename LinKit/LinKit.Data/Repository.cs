using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LinKit.Data;

public abstract class EntityMap<TEntity, TKey> where TEntity : class
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

public class Repository<TEntity, TKey>(DataSession session, EntityMap<TEntity, TKey> map) where TEntity : class
{
    public DataSession Session { get; } = session;
    public EntityMap<TEntity, TKey> Mapping { get; } = map;

    public Query<TEntity> Query() => Session.From(Mapping.Table("entity"));

    public Task<TEntity?> FindAsync(TKey key, CancellationToken ct = default)
        => Query().Where(new Column<TKey>("entity", Mapping.Model.Key).Equal(key)).SingleOrDefaultAsync(ct);

    public Task<TEntity?> GetByIdAsync(TKey id, CancellationToken ct = default) => FindAsync(id, ct);

    public Task<List<TEntity>> ListAsync(int skip = 0, int take = 100, CancellationToken ct = default)
        => Query().Skip(skip).Take(take).ToListAsync(ct);

    public Task<PagedResult<TEntity>> GetPagedAsync(int pageNumber, int pageSize, CancellationToken ct = default)
        => Query().ToPagedListAsync(pageNumber, pageSize, ct);

    public Task<long> CountAsync(CancellationToken ct = default) => Query().CountAsync(ct);

    public Task<bool> ExistsAsync(TKey id, CancellationToken ct = default)
        => Query().Where(new Column<TKey>("entity", Mapping.Model.Key).Equal(id)).AnyAsync(ct);

    public Task<int> DeleteWhereAsync(Predicate predicate, CancellationToken ct = default)
        => Query().Where(predicate).ExecuteDeleteAsync(ct: ct);

    private object?[] WriteValues(TEntity entity, bool insert)
    {
        var values = Mapping.Values(entity);
        for (var index = 0; index < Mapping.Model.Columns.Count; index++)
        {
            var column = Mapping.Model.Columns[index];
            if (column.Tenant)
            {
                if (string.IsNullOrWhiteSpace(Session.TenantId)) throw new InvalidOperationException("A tenant is required for this write.");
                if (!insert && string.IsNullOrEmpty(values[index] as string)) values[index] = Session.TenantId;
                if (!Equals(values[index], Session.TenantId)) throw new InvalidOperationException("Entity tenant does not match session tenant.");
            }
            if (insert && column.Version) values[index] = 1L;
            if (column.SoftDelete)
            {
                var isActive = column.Type == DbType.Boolean
                    ? Equals(values[index], false)
                    : values[index] is null;
                if (!isActive) throw new InvalidOperationException("Normal writes require a non-deleted entity.");
            }
            if (!column.Nullable && !column.Generated && values[index] is null) throw new ArgumentException($"Column '{column.Name}' cannot be null.");
        }
        return values;
    }

    public CommandSpec InsertCommand(TEntity entity)
    {
        var values = WriteValues(entity, true);
        var dialect = Session.Dialect;
        var writer = new SqlWriter(dialect, Session.TenantId);
        var columns = Mapping.Model.Columns.Select((column, index) => (column, index)).Where(item => !item.column.Generated).ToArray();
        var table = dialect.Table(Mapping.Model);
        var key = Mapping.Model.Key;
        string sql;
        if (columns.Length == 0)
        {
            sql = dialect.Kind switch
            {
                DatabaseKind.MySql => $"INSERT INTO {table} () VALUES ()",
                DatabaseKind.Oracle => $"INSERT INTO {table} ({dialect.Quote(key.Name)}) VALUES (DEFAULT)",
                _ => $"INSERT INTO {table}" + (key.Generated && dialect.Kind == DatabaseKind.SqlServer ? $" OUTPUT INSERTED.{dialect.Quote(key.Name)}" : "") + " DEFAULT VALUES"
            };
        }
        else
        {
            var names = string.Join(", ", columns.Select(item => dialect.Quote(item.column.Name)));
            var parameters = string.Join(", ", columns.Select(item => writer.Parameter(values[item.index], item.column)));
            sql = $"INSERT INTO {table} ({names})"
                + (key.Generated && dialect.Kind == DatabaseKind.SqlServer ? $" OUTPUT INSERTED.{dialect.Quote(key.Name)}" : "")
                + $" VALUES ({parameters})";
        }
        if (key.Generated)
        {
            sql += dialect.Kind switch
            {
                DatabaseKind.Sqlite or DatabaseKind.PostgreSql => $" RETURNING {dialect.Quote(key.Name)}",
                DatabaseKind.MySql => "; SELECT LAST_INSERT_ID()",
                DatabaseKind.Oracle => $" RETURNING {dialect.Quote(key.Name)} INTO :generated_key",
                _ => ""
            };
        }
        return new(sql, writer.Arguments);
    }

    public async Task<TKey> InsertAsync(TEntity entity, CancellationToken ct = default)
    {
        var spec = InsertCommand(entity);
        if (Mapping.Model.Key.Generated)
            Mapping.SetKey(entity, await Session.InsertIdentityAsync(spec, Mapping.Model.Key, ct).ConfigureAwait(false));
        else await Session.ExecuteAsync(spec, ct).ConfigureAwait(false);
        Mapping.SetVersion(entity, 1L);
        if (Session.TenantId is not null) Mapping.SetTenant(entity, Session.TenantId);
        return Mapping.Key(entity);
    }

    private string WhereKey(SqlWriter writer, TKey key, long? version)
    {
        var dialect = Session.Dialect;
        var conditions = new List<string> { dialect.Quote(Mapping.Model.Key.Name) + " = " + writer.Parameter(key, Mapping.Model.Key) };
        foreach (var column in Mapping.Model.Columns)
        {
            if (column.Tenant)
            {
                if (string.IsNullOrWhiteSpace(Session.TenantId)) throw new InvalidOperationException("A tenant is required for this write.");
                conditions.Add(dialect.Quote(column.Name) + " = " + writer.Parameter(Session.TenantId, column));
            }
            if (column.SoftDelete)
            {
                if (column.Type == DbType.Boolean)
                    conditions.Add(dialect.Quote(column.Name) + " = " + dialect.False);
                else
                {
                    if (!column.Nullable)
                        throw new InvalidOperationException($"Timestamp soft-delete column '{column.Name}' must be nullable.");
                    conditions.Add(dialect.Quote(column.Name) + " IS NULL");
                }
            }
            if (column.Version)
            {
                if (version is null or < 1) throw new ArgumentException("An original positive concurrency version is required.");
                conditions.Add(dialect.Quote(column.Name) + " = " + writer.Parameter(version, column));
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
                sets.Add(Session.Dialect.Quote(column.Name) + " = " + Session.Dialect.Quote(column.Name) + " + 1");
            }
            else if (!column.PrimaryKey && !column.Generated && !column.Tenant && !column.SoftDelete)
            {
                sets.Add(Session.Dialect.Quote(column.Name) + " = " + writer.Parameter(values[index], column));
            }
        }
        if (sets.Count == 0) throw new NotSupportedException("Entity has no writable columns.");
        var where = WhereKey(writer, Mapping.Key(entity), version);
        var affected = await Session.ExecuteAsync(new($"UPDATE {Session.Dialect.Table(Mapping.Model)} SET {string.Join(", ", sets)} WHERE {where}", writer.Arguments), ct).ConfigureAwait(false);
        if (affected != 1) throw new DataConcurrencyException("Update expected one visible row with the original version.");
        if (version.HasValue) Mapping.SetVersion(entity, checked(version.Value + 1));
    }

    public async Task DeleteAsync(TKey key, long? expectedVersion, CancellationToken ct = default)
    {
        var writer = new SqlWriter(Session.Dialect, Session.TenantId);
        var where = WhereKey(writer, key, expectedVersion);
        var soft = Mapping.Model.Columns.SingleOrDefault(column => column.SoftDelete);
        var version = Mapping.Model.Columns.SingleOrDefault(column => column.Version);
        var table = Session.Dialect.Table(Mapping.Model);
        var deletedValue = soft?.Type == DbType.Boolean ? Session.Dialect.True : "CURRENT_TIMESTAMP";
        var sql = soft is null ? $"DELETE FROM {table} WHERE {where}"
            : $"UPDATE {table} SET {Session.Dialect.Quote(soft.Name)} = {deletedValue}"
                + (version is null ? "" : $", {Session.Dialect.Quote(version.Name)} = {Session.Dialect.Quote(version.Name)} + 1") + $" WHERE {where}";
        if (await Session.ExecuteAsync(new(sql, writer.Arguments), ct).ConfigureAwait(false) != 1)
            throw new DataConcurrencyException("Delete expected one visible row with the original version.");
    }

    public Task DeleteAsync(TKey key, CancellationToken ct = default) => DeleteAsync(key, null, ct);

    private async Task RangeAsync(IEnumerable<TEntity> entities, Func<TEntity, CancellationToken, Task> operation, CancellationToken ct)
    {
        await using var transaction = Session.HasTransaction ? null : await Session.BeginTransactionAsync(ct: ct).ConfigureAwait(false);
        foreach (var entity in entities) await operation(entity, ct).ConfigureAwait(false);
        if (transaction is not null) await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public Task InsertRangeAsync(IEnumerable<TEntity> entities, CancellationToken ct = default)
        => RangeAsync(entities, async (entity, token) => { await InsertAsync(entity, token).ConfigureAwait(false); }, ct);

    public Task UpdateRangeAsync(IEnumerable<TEntity> entities, CancellationToken ct = default)
        => RangeAsync(entities, UpdateAsync, ct);

    public async Task DeleteRangeAsync(IEnumerable<TKey> ids, CancellationToken ct = default)
    {
        await using var transaction = Session.HasTransaction ? null : await Session.BeginTransactionAsync(ct: ct).ConfigureAwait(false);
        foreach (var id in ids) await DeleteAsync(id, null, ct).ConfigureAwait(false);
        if (transaction is not null) await transaction.CommitAsync(ct).ConfigureAwait(false);
    }
}
