using LinKit.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LinKit.Messaging.Tests;

/// <summary>
/// Covers the expression surface added for day-to-day querying (functions, column-to-column comparisons) and the
/// repository conveniences that exist so callers stop hand-rolling N+1 loops. The rendering tests run without a
/// connection on purpose: a portable expression must produce the provider-specific statement before it is executed.
/// </summary>
public sealed class DataSurfaceTests
{
    private const string Schema = """
        CREATE TABLE typed_query_products (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Name TEXT NOT NULL,
            CategoryId INTEGER NULL,
            Price NUMERIC NOT NULL,
            Tags TEXT NULL,
            Notes TEXT NULL,
            DeletedAt TEXT NULL
        );
        CREATE TABLE typed_query_categories (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Name TEXT NOT NULL
        );
        """;

    private sealed class Harness(SqliteConnection anchor, DataSession session) : IAsyncDisposable
    {
        public TypedQueryProductTypedRepository Products { get; } = new(session);

        public async ValueTask DisposeAsync()
        {
            await session.DisposeAsync();
            await anchor.DisposeAsync();
        }
    }

    private static DataSession RenderingSession(DatabaseKind kind = DatabaseKind.Sqlite) =>
        new(
            () =>
                throw new InvalidOperationException(
                    "Command rendering must not open a connection."
                ),
            new SqlDialect(kind)
        );

    private static async Task<Harness> SeedAsync()
    {
        var connectionString =
            $"Data Source=data-surface-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync();
        await using (var command = anchor.CreateCommand())
        {
            command.CommandText = Schema;
            await command.ExecuteNonQueryAsync();
        }

        var session = new DataSession(
            () => new SqliteConnection(connectionString),
            new SqlDialect(DatabaseKind.Sqlite)
        );
        var harness = new Harness(anchor, session);
        await harness.Products.InsertRangeAsync([
            new TypedQueryProduct
            {
                Name = "Alpha",
                CategoryId = 1,
                Price = 10m,
            },
            new TypedQueryProduct
            {
                Name = "Beta",
                CategoryId = 1,
                Price = 20m,
            },
            new TypedQueryProduct { Name = "Gamma", Price = 30m },
        ]);
        return harness;
    }

    [Fact]
    public void PolicyFiltersArePushedIntoTheWhereClauseInsteadOfADerivedTable()
    {
        var session = RenderingSession();
        var table = new TypedQueryProductTypedRepository(session).CreateTable("entity");

        var sql = session.From(table).Where(table.Price.GreaterThanOrEqual(10m)).ToCommand().Sql;

        // A derived table hides the base table from the optimiser. The soft-delete predicate belongs in WHERE.
        Assert.DoesNotContain("SELECT * FROM", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("(SELECT", sql, StringComparison.Ordinal);
        Assert.Contains("\"entity\".\"DeletedAt\" IS NULL", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void ProbesAndCountsCarryThePolicyFilter()
    {
        var session = RenderingSession();
        var table = new TypedQueryProductTypedRepository(session).CreateTable("entity");
        var query = session.From(table).Where(table.Price.GreaterThanOrEqual(10m));

        Assert.Contains(
            "\"entity\".\"DeletedAt\" IS NULL",
            query.AnyCommand().Sql,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "\"entity\".\"DeletedAt\" IS NULL",
            query.CountCommand().Sql,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void JoinPolicyFiltersStayInTheOnClauseSoOuterJoinsKeepTheirSemantics()
    {
        var session = RenderingSession();
        var categories = new TypedQueryCategoryTypedRepository(session);
        var products = new TypedQueryProductTypedRepository(session);
        var categoryTable = categories.CreateTable("category");
        var productTable = products.CreateTable("entity");

        var sql = session
            .From(categoryTable)
            .Join(productTable, categoryTable.Id.Equal(productTable.CategoryId), JoinKind.Left)
            .ToCommand()
            .Sql;

        Assert.DoesNotContain("SELECT * FROM", sql, StringComparison.Ordinal);
        Assert.Contains("LEFT JOIN", sql, StringComparison.Ordinal);
        // In WHERE the filter would silently turn the outer join into an inner join.
        Assert.True(
            sql.IndexOf("\"entity\".\"DeletedAt\" IS NULL", StringComparison.Ordinal)
                > sql.IndexOf(" ON ", StringComparison.Ordinal)
        );
    }

    [Theory]
    [InlineData(DatabaseKind.SqlServer, "LEN(")]
    [InlineData(DatabaseKind.MySql, "CHAR_LENGTH(")]
    [InlineData(DatabaseKind.PostgreSql, "LENGTH(")]
    [InlineData(DatabaseKind.Sqlite, "LENGTH(")]
    [InlineData(DatabaseKind.Oracle, "LENGTH(")]
    public void LengthResolvesTheProviderSpecificName(DatabaseKind kind, string expected)
    {
        var session = RenderingSession(kind);
        var table = new TypedQueryProductTypedRepository(session).CreateTable("entity");

        var sql = session.From(table).Where(Sql.Length(table.Name).GreaterThan(3)).ToCommand().Sql;

        Assert.Contains(expected, sql, StringComparison.Ordinal);
    }

    [Fact]
    public void StringFunctionsAndCoalesceRenderInsidePredicates()
    {
        var session = RenderingSession();
        var table = new TypedQueryProductTypedRepository(session).CreateTable("entity");

        var lowered = session
            .From(table)
            .Where(Sql.Lower(table.Name).Equal("alpha"))
            .ToCommand()
            .Sql;
        Assert.Contains("LOWER(\"entity\".\"Name\")", lowered, StringComparison.Ordinal);

        var coalesced = session
            .From(table)
            .Where(Sql.Coalesce(table.Notes, "none").Equal("none"))
            .ToCommand()
            .Sql;
        Assert.Contains("COALESCE(\"entity\".\"Notes\"", coalesced, StringComparison.Ordinal);
    }

    [Fact]
    public void ColumnToColumnComparisonsBindNoParameters()
    {
        var session = RenderingSession();
        var table = new TypedQueryProductTypedRepository(session).CreateTable("entity");

        var command = session
            .From(table)
            .Where(table.Price.GreaterThanOrEqual(table.Price))
            .ToCommand();

        Assert.Contains(
            "\"entity\".\"Price\" >= \"entity\".\"Price\"",
            command.Sql,
            StringComparison.Ordinal
        );
        Assert.Empty(command.Arguments);
    }

    [Fact]
    public void CountDistinctRendersADistinctAggregate()
    {
        var session = RenderingSession();
        var table = new TypedQueryProductTypedRepository(session).CreateTable("entity");

        var command = session.From(table).AggregateCommand(Sql.CountDistinct(table.CategoryId));

        Assert.Contains(
            "COUNT(DISTINCT \"entity\".\"CategoryId\")",
            command.Sql,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task FindManyLoadsOnlyRequestedKeysAndCollapsesDuplicates()
    {
        await using var harness = await SeedAsync();

        var rows = await harness.Products.FindManyAsync([1, 2, 2]);
        Assert.Equal(2, rows.Count);
        Assert.Equal(["Alpha", "Beta"], rows.Select(row => row.Name).OrderBy(name => name));

        Assert.Empty(await harness.Products.FindManyAsync([]));
    }

    [Fact]
    public async Task PredicateOverloadsReuseTheOptimisedStatements()
    {
        await using var harness = await SeedAsync();
        var table = harness.Products.CreateTable("entity");

        Assert.True(await harness.Products.AnyAsync(table.Price.GreaterThanOrEqual(30m)));
        Assert.False(await harness.Products.AnyAsync(table.Price.GreaterThan(30m)));
        Assert.Equal(2L, await harness.Products.CountAsync(table.Price.GreaterThan(10m)));

        var filtered = await harness.Products.ListAsync(table.Price.GreaterThan(10m));
        Assert.Equal(["Beta", "Gamma"], filtered.Select(row => row.Name).OrderBy(name => name));

        var page = await harness.Products.ListAsync(table.Price.GreaterThan(10m), skip: 1, take: 1);
        Assert.Single(page);
    }

    [Fact]
    public async Task StreamReadsRowsWithoutMaterialisingThem()
    {
        await using var harness = await SeedAsync();

        var names = new List<string>();
        await foreach (var product in harness.Products.StreamAsync())
            names.Add(product.Name);
        Assert.Equal(3, names.Count);

        var limited = new List<string>();
        await foreach (var product in harness.Products.StreamAsync(take: 2))
            limited.Add(product.Name);
        Assert.Equal(2, limited.Count);
    }

    [Fact]
    public async Task ScalarFunctionsExecuteOnSqlite()
    {
        await using var harness = await SeedAsync();
        var table = harness.Products.CreateTable("entity");

        long distinct = await harness
            .Products.Query()
            .ScalarAsync(Sql.CountDistinct(table.CategoryId));
        Assert.Equal(1L, distinct);

        long? length = await harness.Products.Query().ScalarAsync(Sql.Length(table.Name));
        Assert.Equal(5L, length);

        var matches = await harness
            .Products.Query()
            .Where(Sql.Lower(table.Name).Equal("beta"))
            .ToListAsync();
        Assert.Single(matches);

        var coalesced = await harness
            .Products.Query()
            .Where(Sql.Coalesce(table.Notes, "none").Equal("none"))
            .CountAsync();
        Assert.Equal(3L, coalesced);
    }

    [Fact]
    public async Task SoftDeleteKeepsEveryReadShapeConsistent()
    {
        await using var harness = await SeedAsync();

        var alpha = await harness.Products.FindAsync(1);
        Assert.NotNull(alpha);
        await harness.Products.DeleteAsync(alpha!);

        Assert.Null(await harness.Products.FindAsync(1));
        Assert.False(await harness.Products.ExistsAsync(1));
        Assert.Equal(2L, await harness.Products.CountAsync());
        Assert.Equal(2, (await harness.Products.FindManyAsync([1, 2, 3])).Count);
    }

    [Fact]
    public async Task PagingClampsInsteadOfOverflowingOnHugePageNumbers()
    {
        await using var harness = await SeedAsync();

        var page = await harness
            .Products.Query()
            .OrderBy(columns => columns.Id)
            .ToPagedListAsync(int.MaxValue, 100);

        Assert.Equal(3L, page.TotalCount);
        Assert.Empty(page.Items);
    }
}
