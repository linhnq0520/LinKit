using LinKit.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LinKit.Messaging.Tests;

/// <summary>
/// Guards the shapes the fluent pipeline emits. These assertions are deliberately about SQL structure rather than
/// result correctness alone: an existence probe that silently degrades into a full COUNT, or a count that projects
/// every column, still returns the right answer while costing the database far more work than it should.
/// </summary>
public sealed class QueryOptimizationTests
{
    private const string ProductTable = """
        CREATE TABLE typed_query_products (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Name TEXT NOT NULL,
            CategoryId INTEGER NULL,
            Price NUMERIC NOT NULL,
            Tags TEXT NULL,
            Notes TEXT NULL,
            DeletedAt TEXT NULL
        );
        """;

    private static DataSession RenderingSession(DatabaseKind kind = DatabaseKind.Sqlite) =>
        new(
            () =>
                throw new InvalidOperationException(
                    "Command rendering must not open a connection."
                ),
            new SqlDialect(kind)
        );

    private static async Task<(
        SqliteConnection Anchor,
        DataSession Session,
        TypedQueryProductTypedRepository Products
    )> SeedAsync()
    {
        var connectionString =
            $"Data Source=query-optimization-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync();
        await using (var command = anchor.CreateCommand())
        {
            command.CommandText = ProductTable;
            await command.ExecuteNonQueryAsync();
        }

        var session = new DataSession(
            () => new SqliteConnection(connectionString),
            new SqlDialect(DatabaseKind.Sqlite)
        );
        var products = new TypedQueryProductTypedRepository(session);
        await products.InsertRangeAsync([
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
        return (anchor, session, products);
    }

    [Theory]
    [InlineData(DatabaseKind.Sqlite, "SELECT 1 FROM")]
    [InlineData(DatabaseKind.PostgreSql, "SELECT 1 FROM")]
    [InlineData(DatabaseKind.MySql, "SELECT 1 FROM")]
    [InlineData(DatabaseKind.SqlServer, "SELECT TOP 1 1 FROM")]
    [InlineData(DatabaseKind.Oracle, "SELECT 1 FROM")]
    public void AnyRendersAProbeRatherThanACount(DatabaseKind kind, string expectedPrefix)
    {
        var session = RenderingSession(kind);
        var products = new TypedQueryProductTypedRepository(session);
        var table = products.CreateTable("entity");
        var query = session.From(table).Where(table.Price.GreaterThanOrEqual(10m));

        var command = query.AnyCommand();

        Assert.StartsWith(expectedPrefix, command.Sql);
        Assert.DoesNotContain("COUNT", command.Sql, StringComparison.Ordinal);
        Assert.Equal(10m, Assert.Single(command.Arguments).Value);
    }

    [Theory]
    [InlineData(DatabaseKind.Sqlite, "SELECT COUNT(*) FROM")]
    [InlineData(DatabaseKind.PostgreSql, "SELECT COUNT(*) FROM")]
    [InlineData(DatabaseKind.MySql, "SELECT COUNT(*) FROM")]
    [InlineData(DatabaseKind.SqlServer, "SELECT COUNT_BIG(*) FROM")]
    [InlineData(DatabaseKind.Oracle, "SELECT COUNT(*) FROM")]
    public void CountRendersWithoutTheProjection(DatabaseKind kind, string expectedPrefix)
    {
        var session = RenderingSession(kind);
        var products = new TypedQueryProductTypedRepository(session);
        var table = products.CreateTable("entity");
        var query = session
            .From(table)
            .Where(table.Price.GreaterThanOrEqual(10m))
            .OrderBy(table.Price.Asc());

        var command = query.CountCommand();

        Assert.StartsWith(expectedPrefix, command.Sql);
        Assert.DoesNotContain("Name", command.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ORDER BY", command.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("count_result", command.Sql, StringComparison.Ordinal);
        Assert.Equal(10m, Assert.Single(command.Arguments).Value);
    }

    [Fact]
    public void CountWrapsAPagedWindowInsteadOfCountingTheWholeTable()
    {
        var session = RenderingSession();
        var products = new TypedQueryProductTypedRepository(session);
        var table = products.CreateTable("entity");
        var query = session
            .From(table)
            .Where(table.Price.GreaterThanOrEqual(10m))
            .OrderBy(table.Price.Asc())
            .Skip(1)
            .Take(2);

        var sql = query.CountCommand().Sql;

        Assert.StartsWith("SELECT COUNT(*) FROM (SELECT 1 FROM", sql);
        Assert.Contains("count_result", sql, StringComparison.Ordinal);
        Assert.Contains("LIMIT", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void ProbeKeepsTheOffsetOnlyWhenTheQuerySkips()
    {
        var session = RenderingSession();
        var products = new TypedQueryProductTypedRepository(session);
        var table = products.CreateTable("entity");

        var ordered = session
            .From(table)
            .Where(table.Name.Equal("Alpha"))
            .OrderBy(table.Price.Asc());
        Assert.DoesNotContain("ORDER BY", ordered.AnyCommand().Sql, StringComparison.Ordinal);

        var paged = session
            .From(table)
            .Where(table.Name.Equal("Alpha"))
            .OrderBy(table.Price.Asc())
            .Skip(3);
        var pagedSql = paged.AnyCommand().Sql;
        Assert.Contains("ORDER BY", pagedSql, StringComparison.Ordinal);
        Assert.Contains("OFFSET", pagedSql, StringComparison.Ordinal);
    }

    [Fact]
    public void ProbeRespectsAnEmptyTakeWithoutTouchingTheDatabase()
    {
        var session = RenderingSession();
        var products = new TypedQueryProductTypedRepository(session);

        var sql = session.From(products.CreateTable("entity")).Take(0).AnyCommand().Sql;

        Assert.Equal("SELECT 1 WHERE 1 = 0", sql);
    }

    [Fact]
    public void ProbeAndCountCarryThePolicyFilters()
    {
        var session = RenderingSession();
        var products = new TypedQueryProductTypedRepository(session);
        var query = session.From(products.CreateTable("entity"));

        Assert.Contains("\"DeletedAt\" IS NULL", query.AnyCommand().Sql, StringComparison.Ordinal);
        Assert.Contains(
            "\"DeletedAt\" IS NULL",
            query.CountCommand().Sql,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task AnyAsyncAnswersFromASingleRow()
    {
        var (anchor, session, products) = await SeedAsync();
        await using var _ = anchor;
        await using var __ = session;
        var table = products.CreateTable("entity");

        Assert.True(await session.From(table).Where(table.Price.GreaterThan(25m)).AnyAsync());
        Assert.False(await session.From(table).Where(table.Price.GreaterThan(100m)).AnyAsync());
        Assert.False(await session.From(table).Take(0).AnyAsync());
        Assert.True(await session.From(table).OrderBy(table.Price.Asc()).Skip(2).AnyAsync());
        Assert.False(await session.From(table).OrderBy(table.Price.Asc()).Skip(3).AnyAsync());
    }

    [Fact]
    public async Task CountAsyncHonoursFiltersAndPaging()
    {
        var (anchor, session, products) = await SeedAsync();
        await using var _ = anchor;
        await using var __ = session;
        var table = products.CreateTable("entity");

        Assert.Equal(3L, await session.From(table).CountAsync());
        Assert.Equal(
            2L,
            await session.From(table).Where(table.Price.GreaterThanOrEqual(20m)).CountAsync()
        );
        Assert.Equal(2L, await session.From(table).OrderBy(table.Price.Asc()).Skip(1).CountAsync());
        Assert.Equal(
            1L,
            await session.From(table).OrderBy(table.Price.Asc()).Skip(1).Take(1).CountAsync()
        );
        Assert.Equal(
            0L,
            await session.From(table).Where(table.Price.GreaterThan(1000m)).CountAsync()
        );
    }

    [Fact]
    public async Task SingleRowTerminalsReadAtMostWhatTheyNeed()
    {
        var (anchor, session, products) = await SeedAsync();
        await using var _ = anchor;
        await using var __ = session;
        var table = products.CreateTable("entity");

        var ordered = session.From(table).OrderBy(table.Price.Asc());
        Assert.Equal("Alpha", (await ordered.FirstAsync()).Name);
        Assert.Equal("Alpha", (await ordered.FirstOrDefaultAsync())!.Name);
        Assert.Null(
            await session.From(table).Where(table.Price.GreaterThan(1000m)).FirstOrDefaultAsync()
        );

        var single = session.From(table).Where(table.Name.Equal("Gamma"));
        Assert.Equal(30m, (await single.SingleAsync()).Price);
        Assert.Equal(30m, (await single.SingleOrDefaultAsync())!.Price);

        var missing = session.From(table).Where(table.Name.Equal("nobody"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => missing.FirstAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => missing.SingleAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.From(table).Where(table.Price.GreaterThan(0m)).SingleOrDefaultAsync()
        );
    }

    [Fact]
    public async Task ScalarAggregatesUseTheFilteredSet()
    {
        var (anchor, session, products) = await SeedAsync();
        await using var _ = anchor;
        await using var __ = session;
        var table = products.CreateTable("entity");
        var query = session.From(table);

        Assert.Equal(60m, (await query.SumAsync(table.Price))!.Value);
        Assert.Equal(20m, (await query.AverageAsync(table.Price))!.Value);
        Assert.Equal(10m, (await query.MinAsync(table.Price))!.Value);
        Assert.Equal(30m, (await query.MaxAsync(table.Price))!.Value);
        Assert.Equal("Alpha", await query.MinAsync(table.Name));
        Assert.Equal("Gamma", await query.MaxAsync(table.Name));
        Assert.Equal(3L, await query.ScalarAsync(Sql.Count()));
        Assert.Equal(
            50m,
            (await query.Where(table.Price.GreaterThan(15m)).SumAsync(table.Price))!.Value
        );
    }

    [Fact]
    public async Task ScalarAggregatesReturnNullWhenNothingMatches()
    {
        var (anchor, session, products) = await SeedAsync();
        await using var _ = anchor;
        await using var __ = session;
        var table = products.CreateTable("entity");
        var empty = session.From(table).Where(table.Price.GreaterThan(1000m));

        Assert.Null(await empty.SumAsync(table.Price));
        Assert.Null(await empty.AverageAsync(table.Price));
        Assert.Null(await empty.MaxAsync(table.Price));
        Assert.Equal(0L, await empty.CountAsync());
    }

    [Theory]
    [InlineData(DatabaseKind.Sqlite, "SUM(\"entity\".\"Price\")")]
    [InlineData(DatabaseKind.PostgreSql, "SUM(\"entity\".\"Price\")")]
    [InlineData(DatabaseKind.MySql, "SUM(`entity`.`Price`)")]
    [InlineData(DatabaseKind.SqlServer, "SUM([entity].[Price])")]
    [InlineData(DatabaseKind.Oracle, "SUM(\"entity\".\"Price\")")]
    public void ScalarAggregateRendersTheDialectIdentifiers(DatabaseKind kind, string expected)
    {
        var session = RenderingSession(kind);
        var products = new TypedQueryProductTypedRepository(session);
        var table = products.CreateTable("entity");
        var query = session.From(table).Where(table.Price.GreaterThan(0m));

        var command = query.AggregateCommand(Sql.Sum(table.Price));

        Assert.StartsWith("SELECT " + expected + " FROM", command.Sql);
        Assert.Equal(0m, Assert.Single(command.Arguments).Value);
    }

    [Fact]
    public void ScalarAggregateRejectsShapesItCannotAnswer()
    {
        var session = RenderingSession();
        var products = new TypedQueryProductTypedRepository(session);
        var table = products.CreateTable("entity");

        Assert.Throws<NotSupportedException>(() =>
            session.From(table).GroupBy(table.CategoryId).AggregateCommand(Sql.Sum(table.Price))
        );
        Assert.Throws<NotSupportedException>(() =>
            session.From(table).OrderBy(table.Name.Asc()).AggregateCommand(Sql.Sum(table.Price))
        );
        Assert.Throws<NotSupportedException>(() =>
            session.From(table).Take(5).AggregateCommand(Sql.Sum(table.Price))
        );
    }
}
