# LinKit.Data

**LinKit.Data** is a high-performance, source-generated, Native AOT-first ORM and schema synchronization library for .NET 8, .NET 9, and .NET 10. It eliminates heavy runtime reflection and dynamic compilation by generating strongly typed table metadata, reader mappers, projection models, and SQL AST queries at compile time.

LinKit.Data supports **SQLite**, **PostgreSQL**, **SQL Server**, **MySQL**, and **Oracle**.

---

## Design Philosophy: Zero Reflection, Extreme Speed, Native AOT

1. **Declare Domain Model / Entity:** Decorate clean C# POCO classes with intuitive attributes (`[Table]`, `[PrimaryKey]`, `[CompositeIndex]`, `[JsonColumn]`, `[SoftDelete]`, etc.).
2. **Execute Business Logic in CQRS / Web APIs:** Use the **Typed AST ORM** or inject source-generated **`IRepository<TEntity, TId>`** to run type-safe queries, relational joins, set-based updates, or raw SQL with compiled ADO.NET speed.

LinKit does not require EF Core or heavy runtime ORMs. It provides additive schema synchronization and versioned migrations for production workflows.

---

## Key Capabilities

### 1. Compile-Time Typed AST ORM (Zero Reflection)

- **`DataSession`**: Scoped, async-disposable session managing connection lifecycle, transactions, tenancy, and OpenTelemetry instrumentation (`ActivitySource("LinKit.Data")`).
- **`FluentQuery<TResult, TColumns>`**: Fluent query builder operating directly on compile-time generated `{Entity}Table` and `{Entity}OptionalTable` column nodes.
- **Relational Joins**: Type-safe `InnerJoin` and `LeftJoin` across tables with compile-time column safety:
  ```csharp
  var query = session.From(data.Product)
      .InnerJoin(data.Category, (p, c) => p.CategoryId.Equal(c.Id))
      .LeftJoin(data.Supplier, (p, s) => p.SupplierId.Equal(s.Id))
      .Where((p, c, s) => c.Name.Equal("Books") & p.Price >= 15m);
  ```
- **Source-Generated DTO Projections (`[DataRecord]`)**: Generates zero-reflection `{Record}Projection` mappers for fast SQL-to-DTO reading:
  ```csharp
  var dtos = await query
      .Select((p, c) => new ProductSummaryDtoProjection
      {
          ProductName = p.Name,
          CategoryName = c.Name
      })
      .ToListAsync(ct);
  ```

### 2. Set-Based (Bulk) Updates and Deletes

Execute SQL `UPDATE` and `DELETE` directly on the database without loading entities into application memory (`db` is the source-generated `[DataContext]` shown in the usage guide; `db.ProductEntity` is an `IRepository<ProductEntity, int>`):

```csharp
// Set-based delete with a condition
int deleted = await db.ProductEntity
    .DeleteWhereAsync(p => p.CategoryId.Equal(categoryId), ct);

// Set-based update with assignments
int updated = await db.ProductEntity.Query()
    .Where(p => p.CategoryId.Equal(categoryId))
    .ExecuteUpdateAsync(p => [p.Price.Set(29.99m)], ct);
```

### 3. Rich Search & Query Expressions

- **String Search & Functions**: `.Contains(text)`, `.StartsWith(text)`, `.EndsWith(text)`, and `.Like(pattern)` with automatic wildcard escaping (`ESCAPE '!'`). Supports LINQ string functions (`ToLower()`, `ToUpper()`, `Trim()`) translated into native SQL functions (`LOWER`, `UPPER`, `TRIM`).
- **Collections & Ranges**: `.In(params T[] values)`, `.NotIn(...)`, and `.Between(lower, upper)`. Includes automatic 1000-element chunking with `OR` clauses for Oracle database engine compliance (`ORA-01795`).
- **Dynamic Conditions**: `.WhereIf(condition, p => ...)` simplifies conditional filtering in search APIs without awkward `if-else` branching:
  ```csharp
  var paged = await db.ProductEntity.Query()
      .WhereIf(!string.IsNullOrWhiteSpace(query.Keyword), p => p.Name.Contains(query.Keyword!))
      .WhereIf(query.MinPrice.HasValue, p => p.Price >= query.MinPrice!.Value)
      .WhereIf(query.CategoryIds?.Length > 0, p => p.CategoryId.In(query.CategoryIds!))
      .OrderByDescending(p => p.Id)
      .ToPagedListAsync(query.PageIndex, query.PageSize, ct);
  ```
- **Explicit Bounded Reads**: `ToListAsync()` without `Take()` returns **all** matching rows — there is no implicit 100-row cap (previous versions silently truncated at 100). Use `Take(n)` or `ToPagedListAsync` for bounded reads.
- **C# Null Semantics**: LINQ/expression equality follows C# null semantics — `x.Name != value` includes rows where `Name` is `NULL`, and nullable-column comparisons are null-compensated in SQL.
- **EXISTS-style `Any()`**: LINQ `Any()` translates to `SELECT 1 ... LIMIT 1` instead of `COUNT(*)` (except when the query contains `Skip`).

### 4. Native Pagination (`ToPagedListAsync`)

Available directly on `FluentQuery`, `Query`, and `Repository`. Built-in multi-dialect pagination support across SQLite, PostgreSQL (`LIMIT/OFFSET`), SQL Server (`OFFSET/FETCH`), MySQL (`LIMIT/OFFSET`), and Oracle 12c+ (`OFFSET/FETCH` with automatic `ORDER BY (SELECT NULL)` fallback):

```csharp
PagedResult<ProductEntity> result = await repository.Query()
    .Where(p => p.Price.Between(10m, 100m))
    .OrderBy(p => p.Price)
    .ToPagedListAsync(pageNumber: 1, pageSize: 20, ct);

// result.Items, result.TotalCount, result.TotalPages, result.HasPreviousPage, result.HasNextPage
```

### 5. Multi-Database Schema Synchronization & Migrations

- **Auto-Sync Schema (`AutoSyncSchema = true`)**: Automatically inspects database catalog and adds missing tables, columns, indexes, and composite indexes on startup (with robust case-insensitivity handling across catalogs such as Oracle and PostgreSQL). Catalog-query failures in `SchemaSynchronizer` now propagate instead of being silently ignored.
- **Versioned Migrations (`[DataMigration]`)**: Idempotent, ordered data and schema migrations tracked in `__linkit_migrations`.
- **Cross-Instance Migration Lock**: `MigrationRunner` takes a database-level lock so concurrent application instances do not migrate simultaneously — `sp_getapplock` (SQL Server), `pg_advisory_lock` (PostgreSQL), `GET_LOCK` (MySQL). SQLite and Oracle do not take a cross-instance lock.
- **Natural Version Ordering & Error Propagation**: Migration versions are ordered naturally (numeric segments compared as numbers, so `"v9"` runs before `"v10"`), and history-read errors are surfaced instead of swallowed.
- **Escaped Identifiers & Compile-Time Safety**: All schema/DDL dialects escape identifiers and catalog-query literals (quote/backtick/bracket doubling). Table or column names containing quote or bracket characters are rejected at compile time by generator diagnostic **LNKDATA005** (error).

### 6. Raw SQL & Stored Procedure Fallbacks

Ergonomic raw SQL helpers on `DataSession` and `Repository`:

```csharp
// DataSession takes positional SqlArgument values, bound in placeholder order.
int rows = await session.ExecuteSqlAsync(
    "UPDATE items SET active = 1 WHERE group_id = @p0",
    [new SqlArgument(groupId, null)],
    ct);

List<Item> items = await session.QuerySqlAsync(
    "SELECT * FROM items WHERE type = @p0",
    reader => reader.ReadItem(),
    [new SqlArgument(type, null)],
    ct);

// IRepository<TEntity, TKey> takes named parameters from a dictionary or a tuple sequence.
int affected = await db.ItemRepository.ExecuteSqlAsync(
    "UPDATE items SET active = 1 WHERE group_id = @id",
    new Dictionary<string, object?> { ["id"] = groupId },
    ct);
```

Named parameters must come from a `Dictionary<string, object?>` or an `IEnumerable<(string, object?)>`; anonymous
objects are rejected so the call stays Native AOT compatible.

### 7. Query Shapes Tuned for the Database

Every terminal renders the cheapest statement that still answers the question, so the database never does work the
caller throws away:

| Terminal                                                                        | Statement                                                                                                                                    |
| ------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------- |
| `AnyAsync()`                                                                    | `SELECT 1 ... LIMIT 1` (`TOP 1` on SQL Server, `FETCH FIRST 1 ROWS ONLY` on Oracle). Ordering is dropped unless the query pages with `Skip`. |
| `CountAsync()`                                                                  | `SELECT COUNT(*) ... WHERE ...` with **no projection**. A projection-free subquery is only used when the plan groups, is distinct, or pages. |
| `FirstAsync()` / `FirstOrDefaultAsync()`                                        | single-row read, materialised without allocating a list                                                                                      |
| `SingleAsync()` / `SingleOrDefaultAsync()`                                      | at most two rows, which is enough to detect a duplicate without fetching the rest                                                            |
| `SumAsync()` / `AverageAsync()` / `MinAsync()` / `MaxAsync()` / `ScalarAsync()` | one aggregate over the filtered set                                                                                                          |

```csharp
var table = data.Product.CreateTable("entity");
var query = session.From(table).Where(table.Price.GreaterThanOrEqual(10m));

bool exists = await query.AnyAsync(ct);            // SELECT 1 ... LIMIT 1
decimal? total = await query.SumAsync(table.Price); // SELECT SUM("entity"."Price") ...
long rows = await query.CountAsync(ct);            // SELECT COUNT(*) FROM ... WHERE ...
```

`AnyCommand()`, `CountCommand()` and `AggregateCommand(expression)` expose the rendered `CommandSpec`, so the exact
statement can be logged, asserted in tests, or instrumented. Scalar aggregates reject `GroupBy`, `Distinct`, ordering
and paging instead of silently answering a different question.

All of the above stays inside the AOT contract: no `Expression.Compile`, no `Activator.CreateInstance`, and no
`MakeGenericMethod` on the execution path.

### 8. Expression Functions, Predicate Pushdown and Repository Helpers

The fluent surface now covers the comparisons and scalar functions that real queries need, instead of forcing raw SQL:

| Expression                                                                                                 | Renders                                                             |
| ---------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------- |
| `column.Equal(other)` / `NotEqual` / `GreaterThan` / `GreaterThanOrEqual` / `LessThan` / `LessThanOrEqual` | column-to-column comparison, which binds no extra parameter         |
| `Sql.Lower(x)` / `Sql.Upper(x)`                                                                            | `LOWER` / `UPPER`                                                   |
| `Sql.Length(x)`                                                                                            | `LENGTH`, `LEN` on SQL Server, `CHAR_LENGTH` on MySQL               |
| `Sql.Trim(x)`                                                                                              | `TRIM`                                                              |
| `Sql.Coalesce(x, fallback)`                                                                                | `COALESCE`, with the fallback bound using the column's own metadata |
| `Sql.Count(column)` / `Sql.CountDistinct(column)`                                                          | `COUNT(col)` / `COUNT(DISTINCT col)`                                |

```csharp
var table = data.Product.CreateTable("entity");
var matches = await session.From(table)
    .Where(Sql.Lower(table.Name).Contains("phone"))
    .OrderBy(table.Price.Asc())
    .ToListAsync(ct);

long categories = await session.From(table).ScalarAsync(Sql.CountDistinct(table.CategoryId), ct);
```

Tenant and soft-delete predicates are emitted as ordinary `WHERE` conditions, and as `ON` conditions for joined
tables, where they must stay: in `WHERE` they would silently turn an outer join into an inner join. Wrapping the table
in a derived table hid it from the optimiser and prevented index usage.

Repository conveniences exist so callers stop hand-writing N+1 loops:

| Method                                     | Behaviour                                                                      |
| ------------------------------------------ | ------------------------------------------------------------------------------ |
| `FindManyAsync(keys)`                      | one chunked `key IN (...)` statement per parameter batch, duplicates collapsed |
| `AnyAsync(predicate)` / `ExistsAsync(key)` | single-row probe                                                               |
| `CountAsync(predicate)`                    | projection-free count                                                          |
| `ListAsync(predicate, skip, take)`         | filtered, paged read                                                           |
| `StreamAsync(skip, take)`                  | `IAsyncEnumerable` that never buffers the whole result set                     |
| `DeleteAsync(entity)`                      | delete by the entity's key                                                     |

---

## Configuration Attributes

| Attribute                                                                             | Scope             | Description                                                                                                                             |
| :------------------------------------------------------------------------------------ | :---------------- | :-------------------------------------------------------------------------------------------------------------------------------------- |
| `[Table("name", Schema = "...", Comment = "...")]`                                    | Class             | Maps entity to database table with optional schema and documentation comment.                                                           |
| `[PrimaryKey(AutoIncrement = true)]`                                                  | Property          | Designates primary key with optional identity/auto-increment.                                                                           |
| `[Column("col_name", CustomSqlType = "...")]`                                         | Property          | Overrides column name or explicit raw SQL type definition.                                                                              |
| `[MaxLength(200)]`                                                                    | Property          | Defines max length for `VARCHAR(n)` / `NVARCHAR(n)` / `VARBINARY(n)`.                                                                   |
| `[FixedLength(10)]`                                                                   | Property          | Configures fixed character columns: `CHAR(n)` / `NCHAR(n)`.                                                                             |
| `[DecimalPrecision(18, 2)]`                                                           | Property          | Configures precision and scale: `DECIMAL(18, 2)` / `NUMERIC(18, 2)`.                                                                    |
| `[DefaultValueSql("0")]`                                                              | Property          | Sets raw SQL default value expression during column creation.                                                                           |
| `[ForeignKey("other_table", TargetColumn = "Id", OnDelete = OnDeleteAction.Cascade)]` | Property          | Configures foreign key constraint with referential delete actions.                                                                      |
| `[CheckConstraint("Price >= 0", Name = "chk_price")]`                                 | Class / Property  | Enforces SQL check constraint in database DDL.                                                                                          |
| `[Comment("...")]`                                                                    | Class / Property  | Attaches documentation comment in database metadata.                                                                                    |
| `[Unicode(false)]`                                                                    | Property          | Sets string storage to ASCII/ANSI `VARCHAR` vs `NVARCHAR`.                                                                              |
| `[JsonColumn]`                                                                        | Property          | Serializes complex objects/lists to/from JSON/JSONB. AOT-safe when a `JsonSerializerContext` is registered; otherwise reflection-based. |
| `[ConcurrencyCheck]` / `[ConcurrencyToken]`                                           | Property          | Marks the column as an optimistic concurrency token: starts at `1`, filtered on update and incremented.                                 |
| `[IgnoreColumn]`                                                                      | Property          | Excludes the property from column mapping.                                                                                              |
| `[SoftDelete("DeletedAt")]`                                                           | Class             | Enables soft-delete behavior using the specified timestamp or boolean column.                                                           |
| `[MultiTenant("TenantId")]`                                                           | Class             | Declares the tenant column; reads and writes are then scoped to `DataSession.TenantId`.                                                 |
| `[TenantKey]`                                                                         | Property          | Property-level alternative to `[MultiTenant]`; `[MultiTenant]` wins when both are declared.                                             |
| `[Indexed(IsUnique = true, IndexName = "...")]`                                       | Property          | Automatically builds single-column index on startup.                                                                                    |
| `[CompositeIndex("IX_Name_Age", nameof(Name), nameof(Age), IsUnique = true)]`         | Class             | Defines a multi-column unique or non-unique index.                                                                                      |
| `[DataContext]`                                                                       | Class             | Generates typed context containing strongly-typed repositories and schema snapshots.                                                    |
| `[DataRecord]`                                                                        | Class             | Generates zero-reflection DTO projection mappers for typed joins.                                                                       |
| `[DataRepository]`                                                                    | Class / Interface | Generates a typed repository, or implements a declared repository interface.                                                            |
| `[SqlQuery("SELECT ...")]`                                                            | Method            | Marks a repository-interface method that runs the supplied raw SQL.                                                                     |
| `[DataMigration("20261025_01")]`                                                      | Class             | Declares an idempotent database migration.                                                                                              |

> **Where the guards are enforced.** Tenant scoping, optimistic-concurrency checks and soft-delete filtering are
> implemented by the typed repositories a `[DataContext]` exposes (`data.ProductEntity`), which build on
> `Repository<TEntity, TKey>`. The `IRepository<TEntity, TKey>` that dependency injection resolves for a plain
> `[Table]` entity is a minimal CRUD repository: it filters soft-deleted rows but does **not** evaluate
> `[MultiTenant]`/`[TenantKey]` or `[ConcurrencyCheck]`/`[ConcurrencyToken]`. For a tenant-scoped or versioned entity,
> resolve the generated `{Entity}TypedRepository` or the `[DataContext]` instead of `IRepository<TEntity, TKey>`.

---

## Installation

```shell
dotnet add package LinKit.Data
```

Add your database ADO.NET driver:

```shell
# SQLite
dotnet add package Microsoft.Data.Sqlite

# PostgreSQL
dotnet add package Npgsql

# SQL Server
dotnet add package Microsoft.Data.SqlClient

# MySQL
dotnet add package MySqlConnector

# Oracle
dotnet add package Oracle.ManagedDataAccess.Core
```

---

## Usage Guide

### 1. Declare Domain Entities

```csharp
using LinKit.Data;

[Table("catalog_products", Comment = "Catalog inventory table")]
[CompositeIndex("IX_Product_Sku", nameof(Sku), IsUnique = true)]
[SoftDelete("DeletedAt")]
public class ProductEntity
{
    [PrimaryKey(AutoIncrement = true)]
    public int Id { get; set; }

    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(50)]
    public string Sku { get; set; } = string.Empty;

    [DecimalPrecision(18, 2)]
    public decimal Price { get; set; }

    public int? CategoryId { get; set; }

    [JsonColumn]
    public List<string>? Tags { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }
}

[Table("catalog_categories")]
public class CategoryEntity
{
    [PrimaryKey(AutoIncrement = true)]
    public int Id { get; set; }

    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;
}
```

### 2. Configure in `Program.cs`

```csharp
using LinKit.Data;
using Npgsql; // or Microsoft.Data.Sqlite, Microsoft.Data.SqlClient, etc.

var builder = WebApplication.CreateSlimBuilder(args);

// Register LinKit.Data with automatic schema synchronization
builder.Services.AddLinKitData(options =>
{
    // PostgreSQL:
    options.UseConnection(DatabaseDialect.PostgreSql, () => new NpgsqlConnection(connStr));

    // SQLite:
    // options.UseConnection(DatabaseDialect.Sqlite, () => new SqliteConnection("Data Source=app.db"));

    // SQL Server:
    // options.UseConnection(DatabaseDialect.SqlServer, () => new SqlConnection(connStr));

    options.AutoSyncSchema = true; // Automatically creates tables, columns, and indexes at startup
});
```

### 3. Declare DataContext & DTO Projection (Optional)

```csharp
[DataContext(typeof(ProductEntity), typeof(CategoryEntity))]
public partial class CatalogDataContext;

[DataRecord]
public record ProductSummaryDto
{
    public string ProductName { get; init; } = string.Empty;
    public string CategoryName { get; init; } = string.Empty;
    public decimal Price { get; init; }
}
```

### 4. Querying in Web APIs / CQRS Handlers

#### A. Strongly Typed Join & Projection

```csharp
[CqrsHandler]
public class SearchProductsHandler(CatalogDataContext db)
    : IQueryHandler<SearchProductsQuery, List<ProductSummaryDto>>
{
    public async Task<List<ProductSummaryDto>> HandleAsync(SearchProductsQuery query, CancellationToken ct)
    {
        return await db.ProductEntity.Query()
            .InnerJoin(db.CategoryEntity, (p, c) => p.CategoryId.Equal(c.Id))
            .Where((p, c) => c.Name.Contains(query.Category) & p.Price >= query.MinPrice)
            .OrderByDescending((p, _) => p.Price)
            .Select((p, c) => new ProductSummaryDtoProjection
            {
                ProductName = p.Name,
                CategoryName = c.Name,
                Price = p.Price
            })
            .ToListAsync(ct);
    }
}
```

#### B. Dynamic Filtering & Pagination

```csharp
[CqrsHandler]
public class FilterProductsHandler(ProductEntityTypedRepository repository)
    : IQueryHandler<FilterProductsQuery, PagedResult<ProductEntity>>
{
    public async Task<PagedResult<ProductEntity>> HandleAsync(FilterProductsQuery query, CancellationToken ct)
    {
        return await repository.Query()
            .WhereIf(!string.IsNullOrWhiteSpace(query.Keyword), p => p.Name.Contains(query.Keyword!) | p.Sku.StartsWith(query.Keyword!))
            .WhereIf(query.MinPrice.HasValue, p => p.Price >= query.MinPrice!.Value)
            .WhereIf(query.CategoryIds?.Length > 0, p => p.CategoryId.In(query.CategoryIds!))
            .OrderByDescending(p => p.Id)
            .ToPagedListAsync(query.PageIndex, query.PageSize, ct);
    }
}
```

#### C. Bulk Delete (Set-Based)

```csharp
// Execute SQL: DELETE FROM catalog_products WHERE category_id = @p0
int affectedRows = await repository.DeleteWhereAsync(p => p.CategoryId.Equal(categoryId), ct);
```

---

## Unit of Work & Transactions

```csharp
await using var tx = await db.Session.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
try
{
    await db.ProductEntity.InsertAsync(newProduct, ct);
    await db.CategoryEntity.UpdateAsync(category, ct);
    await tx.CommitAsync(ct);
}
catch
{
    // Disposing without a successful commit rolls the transaction back.
    throw;
}
```

To monitor transaction completion lifecycle, register `IUnitOfWorkInterceptor`:

```csharp
builder.Services.AddScoped<IUnitOfWorkInterceptor, AuditUnitOfWorkInterceptor>();
```

---

## License

Licensed under the [MIT License](https://github.com/linhnq0520/LinKit/blob/main/LICENSE).

