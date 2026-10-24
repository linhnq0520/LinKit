# LinKit.Data

[![NuGet Version](https://img.shields.io/nuget/v/LinKit.Data.svg)](https://www.nuget.org/packages/LinKit.Data/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/LinKit.Data.svg)](https://www.nuget.org/packages/LinKit.Data/)

**LinKit.Data** is a source-generated ADO.NET data access and schema synchronization library. Generated repository and reader-mapping paths are designed to reduce runtime discovery. LINQ closure extraction and other specific paths may still use runtime reflection/dynamic code; the repository AOT smoke app covers only a selected feature set.

---

## 💡 The Philosophy: Developer Does Only 2 Things

1. **Declare Domain Model / Entity:** Add, remove, or modify properties and configuration attributes directly on C# classes.
2. **Execute Business Logic in CQRS Handlers:** Inject source-generated `IRepository<TEntity, TId>` and perform CRUD, raw SQL execution, or fluent queries with blazing-fast compiled ADO.NET speed.

LinKit does not require EF Core. It provides additive schema synchronization and versioned migrations for changes that need explicit data/schema operations.

---

## 🚀 Key Features

- **Additive schema synchronization:** At application startup, LinKit can create missing tables and add supported missing columns/indexes for SQLite, PostgreSQL, SQL Server, MySQL, and Oracle. It is not a general migration engine and does not safely infer renames, drops, or arbitrary existing-column changes.
- **EF Core Equivalent Attributes:** Configure lengths, precisions, scales, foreign keys, check constraints, comments, unicode, json columns, and optimistic concurrency.
- **Compile-Time Code Generation:** Repository implementations, DataReader mapping, and standard SQL operations are generated at compile time. Custom LINQ closure values are currently extracted from expression trees using runtime member access; this path is exercised under Native AOT but is not reflection-free.
- **Native AOT JSON Serialization (`[JsonColumn]`):**
  - **Compile-time Specific Context:** `[JsonColumn(typeof(AppJsonContext))]` can bind to `AppJsonContext.Default` for the configured type.
  - **Ambient Global Context:** `options.UseJsonSerializerContext(AppJsonContext.Default)` across repositories.
- **Explicit query parameter collections:**
  - Generated `QuerySqlAsync` / `ExecuteSqlAsync` paths support `IEnumerable<KeyValuePair<string, object?>>`, `IEnumerable<(string Name, object? Value)>`, and `IEnumerable<DbParameter>` without reflecting over anonymous parameter objects.
  - Anonymous parameter objects are not supported by generated raw-SQL repositories. Use one of the explicit parameter collection shapes above.
- **Full CLR & Database Type Coverage:** Built-in code generation for `int`, `long`, `short`, `byte`, `sbyte`, `uint`, `ulong`, `ushort`, `bool`, `char`, `decimal`, `double`, `float`, `Guid`, `DateTime`, `DateTimeOffset`, `DateOnly`, `TimeOnly`, `TimeSpan`, `byte[]`, `Enum`, `JsonColumn`, and their Nullable equivalents.
- **Comprehensive Repository Abstractions:**
  - Standard CRUD: `GetByIdAsync`, `ListAsync`, `InsertAsync`, `UpdateAsync`, `DeleteAsync`
  - Bulk Operations: `InsertRangeAsync`, `UpdateRangeAsync`, `DeleteRangeAsync`
  - Pagination: `GetPagedAsync(pageNumber, pageSize)` returning `PagedResult<T>`
  - Arbitrary SQL Execution: `QuerySqlAsync`, `QuerySingleSqlAsync`, `ExecuteSqlAsync`, `ExecuteScalarSqlAsync`
  - Fluent Query Builder: `repo.Query().Where(...).OrderBy(...).Skip(...).Take(...).ToPagedListAsync(...)`
- **Unit of Work & Ambient Transactions:** Repositories using the same scoped UoW can enlist commands in its current transaction.
- **Partial generated repositories:** Generated repository implementations are `partial`, so application code in the same project can add focused helpers without editing generated files.

> Raw SQL and string-based fluent conditions are intentionally passed through to the database. Always bind untrusted values as parameters; never concatenate user input into SQL text. Prefer the generated LINQ query API where it supports the required expression.

The built-in soft-delete filter applies to generated repository and fluent/LINQ queries. Raw SQL and custom `[SqlQuery]` repository methods are application-controlled and must include the appropriate soft-delete predicate themselves. When combining multiple fluent `.Where(...)` calls, use unique parameter names across all calls (for example, `@categoryId` and `@supplierId`); the query builder does not rewrite SQL parameter names.

---

## 🏷️ Comprehensive Configuration Attributes

| Attribute | Scope | Description |
| :--- | :--- | :--- |
| `[Table("name", Schema = "...", Comment = "...")]` | Class | Maps entity to database table with optional schema and documentation comment. |
| `[PrimaryKey(AutoIncrement = true)]` | Property | Designates primary key with optional identity/auto-increment. |
| `[Column("col_name", CustomSqlType = "...")]` | Property | Overrides column name or explicit raw SQL type definition. |
| `[MaxLength(200)]` | Property | Defines max length for `VARCHAR(n)` / `NVARCHAR(n)` / `VARBINARY(n)`. |
| `[FixedLength(10)]` | Property | Configures fixed character columns: `CHAR(n)` / `NCHAR(n)`. |
| `[DecimalPrecision(18, 2)]` | Property | Configures precision and scale: `DECIMAL(18, 2)` / `NUMERIC(18, 2)`. |
| `[DefaultValueSql("0")]` | Property | Sets raw SQL default value expression during column creation. |
| `[ForeignKey("other_table", TargetColumn = "Id", OnDelete = OnDeleteAction.Cascade)]` | Property | Configures foreign key constraint with referential delete actions. |
| `[CheckConstraint("Price >= 0", Name = "chk_price")]` | Class / Property | Enforces SQL check constraint in database DDL. |
| `[Comment("...")]` | Class / Property | Attaches documentation comment in database metadata. |
| `[Unicode(false)]` | Property | Sets string storage to ASCII/ANSI `VARCHAR` vs `NVARCHAR`. |
| `[JsonColumn]` | Property | Automatically serializes complex objects/lists to/from JSON/JSONB. |
| `[ConcurrencyCheck]` | Property | Marks column for optimistic concurrency tracking. |
| `[IgnoreColumn]` | Property | Excludes property from database mapping. |
| `[Indexed(IsUnique = true, IndexName = "...")]` | Property | Automatically builds single-column index on startup. |
| `[CompositeIndex("IX_Name_Age", nameof(Name), nameof(Age), IsUnique = true)]` | Class | Defines a multi-column index. |
| `[SoftDelete("DeletedAt")]` | Class | Enables soft-delete behavior using the specified timestamp column. Map that column as a nullable entity property or create it in a migration; the generator warns if it is missing. |
| `[MultiTenant("TenantId")]` | Class | Records tenant-column metadata only. It does **not** enforce tenant isolation; the generator emits a warning. Add explicit tenant predicates to every query. |
| `[DataRepository]` | Interface | Directs source generator to implement custom repository interfaces. |
| `[SqlQuery("SELECT ...")]` | Method | Declarative custom query method generation. |

---

## 📦 Installation

```shell
dotnet add package LinKit.Data
```

Add your database ADO.NET driver:

```shell
# For SQLite
dotnet add package Microsoft.Data.Sqlite

# For PostgreSQL
dotnet add package Npgsql

# For SQL Server
dotnet add package Microsoft.Data.SqlClient

# For MySQL
dotnet add package MySqlConnector

# For Oracle
dotnet add package Oracle.ManagedDataAccess.Core
```

---

## 🛠️ Usage Example

### 1. Declare Domain Entity

```csharp
using LinKit.Data;

public enum ProductStatus { Draft = 0, Active = 1, Archived = 2 }

[Table("catalog_products", Comment = "Catalog inventory table")]
[CheckConstraint("Price >= 0", Name = "chk_product_price_positive")]
public class ProductEntity
{
    [PrimaryKey]
    public Guid Id { get; set; }

    [Indexed]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [DecimalPrecision(18, 2)]
    public decimal Price { get; set; }

    [DefaultValueSql("0")]
    public int Stock { get; set; } = 0;

    [MaxLength(50)]
    public string? Sku { get; set; }

    public bool IsFeatured { get; set; } = false;

    public ProductStatus Status { get; set; } = ProductStatus.Active;

    public DateOnly? ReleasedDate { get; set; }

    public TimeOnly? DailyRestockTime { get; set; }

    [JsonColumn]
    public List<string>? Tags { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
```

### 2. Configure in `Program.cs`

```csharp
using LinKit.Data;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);

// Register LinKit.Data with automatic schema synchronization
builder.Services.AddLinKitData(options =>
{
    // SQLite:
    options.UseConnection(DatabaseDialect.Sqlite, () => new SqliteConnection("Data Source=app.db"));
    
    // Or PostgreSQL:
    // options.UseConnection(DatabaseDialect.PostgreSql, () => new NpgsqlConnection(connStr));
    
    // Or SQL Server:
    // options.UseConnection(DatabaseDialect.SqlServer, () => new SqlConnection(connStr));

    // Or MySQL:
    // options.UseConnection(DatabaseDialect.MySql, () => new MySqlConnection(connStr));

    // Or Oracle:
    // options.UseConnection(DatabaseDialect.Oracle, () => new OracleConnection(connStr));

    options.AutoSyncSchema = true; // Creates missing schema objects and adds supported missing columns.
});
```

### 3. Inject Repository in CQRS Handlers

**Option A: LINQ Query Comprehension (`from ... in ... select`)**
```csharp
using LinKit.Core.Cqrs;
using LinKit.Data;

[CqrsHandler]
public class GetFeaturedProductsHandler(IRepository<ProductEntity, Guid> repository) 
    : IQueryHandler<GetFeaturedProductsQuery, List<ProductEntity>>
{
    public async Task<List<ProductEntity>> HandleAsync(GetFeaturedProductsQuery query, CancellationToken ct)
    {
        // Supports only the LINQ expression subset implemented by LinKit.Data.
        var q = from p in repository.AsQueryable()
                where p.Price >= query.MinPrice && p.IsFeatured
                orderby p.Price descending
                select p;

        return await q.ToListAsync(ct);
    }
}
```

**Option B: Fluent Query Builder**
```csharp
using LinKit.Core.Cqrs;
using LinKit.Data;

[CqrsHandler]
public class GetProductsPagedHandler(IRepository<ProductEntity, Guid> repository) 
    : IQueryHandler<GetProductsPagedQuery, PagedResult<ProductEntity>>
{
    public async Task<PagedResult<ProductEntity>> HandleAsync(GetProductsPagedQuery query, CancellationToken ct)
    {
        return await repository.Query()
            .Where("Price >= @minPrice", new[] { (Name: "minPrice", Value: (object?)query.MinPrice) })
            .OrderBy("Price")
            .ToPagedListAsync(query.Page, query.PageSize, ct);
    }
}
```

---

### 4. Versioned Data & Schema Migrations (`[DataMigration]`)

For master data seeding, backfills, or intentional schema alterations (rename column, drop column):

```csharp
using LinKit.Data;

[DataMigration("20261021_01", Description = "Seed initial catalog products and categories")]
public class SeedCatalogInitialProductsMigration : IDataMigration
{
    public async Task UpAsync(IMigrationContext context, CancellationToken ct = default)
    {
        // 1. Safe DDL operations across all 5 databases (if needed)
        // await context.RenameColumnAsync("catalog_products", "OldSku", "Sku", ct);
        // await context.DropColumnAsync("catalog_products", "ObsoleteColumn", ct);

        // 2. Check existence & Seed initial data
        if (!await context.ExistsAsync(
            "catalog_products",
            "\"Sku\" = @sku",
            new Dictionary<string, object?> { ["sku"] = "SEED-001" },
            ct))
        {
            var repo = context.GetRepository<ProductEntity, Guid>();
            await repo.InsertAsync(new ProductEntity
            {
                Id = Guid.NewGuid(),
                Name = "Master Seed Product",
                Price = 149.99m,
                Sku = "SEED-001",
                Status = ProductStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow
            }, ct);
        }
    }
}
```

- **Compile-time Discovery**: Migration registrations are generated by `LinKit.Data.Generator`; the complete migration and provider path still requires application-level AOT validation.
- **Idempotent Execution**: History tracked in `__linkit_migrations` table and executed once in exact version order inside a transaction.

---

## 🔄 Unit of Work Lifecycle Hooks

Register scoped `IUnitOfWorkInterceptor` implementations to observe transaction completion:

```csharp
builder.Services.AddScoped<IUnitOfWorkInterceptor, AuditUnitOfWorkInterceptor>();
```

`BeforeCommitAsync` runs sequentially in registration order while the transaction is active. Throwing from it prevents the commit; LinKit.Data attempts rollback and invokes `AfterRollbackAsync` if rollback succeeds. Use the supplied `DbConnection` and `DbTransaction` directly inside this hook rather than calling another `IUnitOfWork` method.

`AfterCommitAsync` runs after the transaction is committed and disposed, outside the Unit of Work lock. `AfterRollbackAsync` similarly runs after a successful rollback. All registered post-transaction hooks are attempted even if one fails; failures are reported as `UnitOfWorkHookException` (or an `AggregateException` if cleanup also failed). In particular, `UnitOfWorkHookException.TransactionCommitted` indicates that the commit succeeded and cannot be undone—do not blindly retry the business transaction. For reliable external side effects, use the transactional outbox rather than relying on an in-process after-commit callback.

---

## 🧩 Extending Generated Data Types

LinKit-generated repositories are emitted into the consuming project's `RootNamespace`, so their generated declarations can be extended with another `partial` declaration in that same project:

```csharp
namespace MyApp;

public partial class ProductEntityRepository
{
    public Task<int> CountAvailableAsync(CancellationToken ct = default) =>
        ExecuteScalarSqlAsync<int>(
            "SELECT COUNT(*) FROM catalog_products WHERE Stock > 0",
            parameters: null,
            ct: ct);
}
```

To inject the concrete repository directly, register it in application DI:

```csharp
builder.Services.AddScoped<ProductEntityRepository>();
```

Every `[Table]` entity automatically gets a typed repository interface and generated repository registration; no repository attribute is required. LinKit uses the project `RootNamespace`, names the interface `I{EntityClassName}Repository`, and names the concrete class `{EntityClassName}Repository` (for example, `ProductEntity` produces `IProductEntityRepository` and `ProductEntityRepository`):

```csharp
[Table("catalog_products")]
public class ProductEntity
{
}
```

```csharp
namespace MyApp;

[DataRepository]
public partial interface IProductEntityRepository
{
    Task<long> CountAvailableAsync(CancellationToken ct = default);
}
```

```csharp
namespace MyApp;

public partial class ProductEntityRepository
{
    public Task<long> CountAvailableAsync(CancellationToken ct = default) =>
        Query().Where("Stock > 0").CountAsync(ct);
}
```

Generated infrastructure types such as mapper extensions, the schema provider, and the migration registry use `RootNamespace.Generated`. The `AddLinKitData` extension remains in `LinKit.Data`.

The generated `IProductEntityRepository` is a `partial interface` that inherits `IRepository<ProductEntity, Guid>`. LinKit also generates `ProductEntityRepository : IProductEntityRepository` and registers `IProductEntityRepository` to that implementation in `AddLinKitData`. A matching application partial interface named with the `Repository` suffix can declare custom methods without an attribute. Methods without `[SqlQuery]` are implemented in the repository's application-side `partial class`; methods with `[SqlQuery]` are generated by LinKit.Data. Keep `[DataRepository]` on a custom interface only when its name does not follow the `*Repository` convention and it needs generator-discovered query methods. The optional entity-level `[DataRepository("ICatalogProductRepository", Namespace = "MyApp.Catalog")]` overrides the default interface name and namespace. `partial` parts must be compiled into the consuming assembly; it does not let an application add source to types shipped inside the LinKit.Data NuGet assembly.

---

## 📄 License
Licensed under the [MIT License](https://github.com/linhnq0520/LinKit/blob/main/LICENSE).
