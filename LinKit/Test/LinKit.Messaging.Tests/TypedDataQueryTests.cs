using LinKit.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Npgsql;
using System.Data.Common;
using System.Text.Json.Serialization;
using Xunit;

namespace LinKit.Messaging.Tests;

public sealed class TypedDataQueryTests
{
    [ProviderFact("LINKIT_TEST_POSTGRES")]
    public Task GeneratedQueriesAndDmlExecuteAgainstPostgreSql() =>
        RunProviderQueryScenarioAsync(
            DatabaseKind.PostgreSql,
            () => new NpgsqlConnection(Environment.GetEnvironmentVariable("LINKIT_TEST_POSTGRES")));

    [ProviderFact("LINKIT_TEST_SQLSERVER")]
    public Task GeneratedQueriesAndDmlExecuteAgainstSqlServer() =>
        RunProviderQueryScenarioAsync(
            DatabaseKind.SqlServer,
            () => new SqlConnection(Environment.GetEnvironmentVariable("LINKIT_TEST_SQLSERVER")));

    private static async Task RunProviderQueryScenarioAsync(
        DatabaseKind kind,
        Func<DbConnection> createConnection)
    {
        var dialect = new SqlDialect(kind);
        var tableModels = new[]
        {
            TypedQueryCategoryMap.TableModel,
            TypedQueryProductMap.TableModel,
            TypedQueryHardDeleteMap.TableModel
        };

        await using (var connection = createConnection())
        {
            await connection.OpenAsync();
            foreach (var model in tableModels.Reverse())
            {
                await using var drop = connection.CreateCommand();
                drop.CommandText = $"DROP TABLE IF EXISTS {dialect.Table(model)}";
                await drop.ExecuteNonQueryAsync();
            }

            foreach (var model in tableModels)
            {
                await using var create = connection.CreateCommand();
                create.CommandText = dialect.CreateTable(model);
                await create.ExecuteNonQueryAsync();
            }
        }

        try
        {
            await using var session = new DataSession(createConnection, dialect);
            var products = new TypedQueryProductTypedRepository(session);
            var categories = new TypedQueryCategoryTypedRepository(session);
            var category = new TypedQueryCategory { Name = "Provider Books" };
            await categories.InsertAsync(category);

            var first = new TypedQueryProduct { Name = "Provider Alpha", CategoryId = category.Id, Price = 12m };
            var second = new TypedQueryProduct { Name = "Provider Beta", CategoryId = category.Id, Price = 20m };
            var uncategorized = new TypedQueryProduct { Name = "Provider Gamma", Price = 5m };
            await products.InsertRangeAsync([first, second, uncategorized]);

            var filtered = await products.Query()
                .Where(product => product.Price >= 10m & product.Name.Contains("Provider"))
                .OrderByDescending(product => product.Price)
                .ToListAsync();
            Assert.Equal(["Provider Beta", "Provider Alpha"], filtered.Select(product => product.Name));

            var joined = await products.Query()
                .InnerJoin(categories, (product, joinedCategory) => product.CategoryId.Equal(joinedCategory.Id))
                .Where((product, joinedCategory) =>
                    joinedCategory.Name == "Provider Books" & product.Price >= 10m)
                .Select((product, joinedCategory) => new Projection<TypedQueryProductCategory>(
                    [product.Name, joinedCategory.Name],
                    reader => new TypedQueryProductCategory(
                        DbValues.Read<string>(reader, 0),
                        DbValues.Read<string>(reader, 1))))
                .ToListAsync();
            Assert.Equal(
                [new TypedQueryProductCategory("Provider Alpha", "Provider Books"),
                    new TypedQueryProductCategory("Provider Beta", "Provider Books")],
                joined.OrderBy(row => row.Product));

            var aggregates = await products.Query()
                .WhereExists(categories, (product, existingCategory) =>
                    product.CategoryId.Equal(existingCategory.Id))
                .LeftJoin(categories, (product, joinedCategory) =>
                    product.CategoryId.Equal(joinedCategory.Id))
                .GroupBy((product, joinedCategory) => [joinedCategory.Id, joinedCategory.Name])
                .Having((product, _) => Sql.Sum(product.Price).GreaterThanOrEqual(10m))
                .Select((product, joinedCategory) => new Projection<TypedQueryCategoryTotalWithName>(
                    [joinedCategory.Name, Sql.Count(), Sql.Sum(product.Price)],
                    reader => new TypedQueryCategoryTotalWithName(
                        DbValues.Read<string?>(reader, 0),
                        DbValues.Read<long>(reader, 1),
                        DbValues.Read<decimal?>(reader, 2))))
                .ToListAsync();
            Assert.Equal(
                new TypedQueryCategoryTotalWithName("Provider Books", 2, 32m),
                Assert.Single(aggregates));

            var page = await products.Query()
                .Where(product => product.Price >= 10m)
                .OrderBy(product => product.Price)
                .ToPagedListAsync(2, 1);
            Assert.Equal(2, page.TotalCount);
            Assert.Equal("Provider Beta", Assert.Single(page.Items).Name);

            Assert.Equal(
                1,
                await products.Query()
                    .Where(product => product.Id == second.Id)
                    .ExecuteUpdateAsync(columns => [columns.Name.Set("Provider Beta updated")]));
            Assert.Equal("Provider Beta updated", (await products.GetByIdAsync(second.Id))!.Name);

            await products.DeleteAsync(first.Id);
            Assert.Null(await products.GetByIdAsync(first.Id));

            var hardDelete = new TypedQueryHardDeleteTypedRepository(session);
            await hardDelete.InsertAsync(new TypedQueryHardDelete { Id = 1, Name = "remove" });
            Assert.Equal(
                1,
                await hardDelete.Query()
                    .Where(row => row.Name == "remove")
                    .ExecuteDeleteAsync());
            Assert.Null(await hardDelete.GetByIdAsync(1));
        }
        finally
        {
            await using var connection = createConnection();
            await connection.OpenAsync();
            foreach (var model in tableModels.Reverse())
            {
                await using var drop = connection.CreateCommand();
                drop.CommandText = $"DROP TABLE IF EXISTS {dialect.Table(model)}";
                await drop.ExecuteNonQueryAsync();
            }
        }
    }

    [Fact]
    public async Task GeneratedEntityProjectionJoinAggregateAndTimestampSoftDeleteExecuteOnSqlite()
    {
        var connectionString = $"Data Source=typed-query-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync();
        await using (var command = anchor.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE typed_query_categories (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL
                );
                CREATE TABLE typed_query_products (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL,
                    CategoryId INTEGER NULL,
                    Price NUMERIC NOT NULL,
                    Tags TEXT NULL,
                    Notes TEXT NULL,
                    DeletedAt TEXT NULL
                );
                CREATE TABLE typed_query_hard_delete (
                    Id INTEGER PRIMARY KEY,
                    Name TEXT NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync();
        }

        await using var session = new DataSession(
            () => new SqliteConnection(connectionString),
            new SqlDialect(DatabaseKind.Sqlite));
        var products = new TypedQueryProductTypedRepository(session);
        var categories = new TypedQueryCategoryTypedRepository(session);
        var category = new TypedQueryCategory { Name = "Books" };
        await categories.InsertAsync(category);

        var first = new TypedQueryProduct { Name = "A", CategoryId = category.Id, Price = 12m, Tags = ["read"], Notes = "archived" };
        var second = new TypedQueryProduct { Name = "B", CategoryId = category.Id, Price = 20m };
        var uncategorized = new TypedQueryProduct { Name = "C", Price = 5m };
        await products.InsertRangeAsync([first, second, uncategorized]);

        var minimumPrice = 10m;
        var entities = await products.Query()
            .Where(product => product.Price >= minimumPrice)
            .OrderByDescending(product => product.Price)
            .ToListAsync();

        Assert.Equal(["B", "A"], entities.Select(product => product.Name));
        Assert.Equal("archived", entities.Single(product => product.Name == "A").Notes);
        Assert.Null(entities.Single(product => product.Name == "B").Notes);
        Assert.Equal(["read"], entities.Single(product => product.Name == "A").Tags);
        Assert.Single(await products.Query().Where(product => product.Name.Contains("A")).ToListAsync());
        Assert.Single(await products.Query().Where(product => product.Name.StartsWith("A")).ToListAsync());
        Assert.Single(await products.Query().Where(product => product.Name.EndsWith("A")).ToListAsync());
        Assert.Equal(
            ["B", "C"],
            (await products.Query().Where(product => product.Notes.NotIn("archived")).OrderBy(product => product.Name).ToListAsync())
                .Select(product => product.Name));
        Assert.Equal(
            ["C"],
            (await products.Query().Where(product => !(product.CategoryId > 0)).ToListAsync())
                .Select(product => product.Name));
        Assert.False(TypedQueryProductMap.TableModel.Columns.Single(column => column.Name == "Name").Nullable);
        Assert.True(TypedQueryProductMap.TableModel.Columns.Single(column => column.Name == "Notes").Nullable);

        var categoryName = "Books";
        var joined = await products.Query()
            .InnerJoin(categories, (product, joinedCategory) => product.CategoryId.Equal(joinedCategory.Id))
            .Where((product, joinedCategory) =>
                joinedCategory.Name == categoryName
                & product.Price >= minimumPrice)
            .OrderByDescending((product, _) => product.Price)
            .ThenBy((product, _) => product.Id)
            .Select((product, joinedCategory) => new Projection<TypedQueryProductCategory>(
                new SqlExpression[] { product.Name, joinedCategory.Name },
                reader => new TypedQueryProductCategory(
                    DbValues.Read<string>(reader, 0),
                    DbValues.Read<string>(reader, 1))))
            .ToListAsync();

        Assert.Equal(
            [new TypedQueryProductCategory("B", "Books"), new TypedQueryProductCategory("A", "Books")],
            joined);

        var totals = await products.Query()
            .GroupBy(columns => [columns.CategoryId])
            .Select(columns => new Projection<TypedQueryCategoryTotal>(
                new SqlExpression[] { columns.CategoryId, Sql.Sum(columns.Price) },
                reader => new TypedQueryCategoryTotal(
                    DbValues.Read<int?>(reader, 0),
                    DbValues.Read<decimal?>(reader, 1))))
            .ToListAsync();

        Assert.Equal(32m, Assert.Single(totals, total => total.CategoryId == category.Id).Total);
        Assert.Equal(5m, Assert.Single(totals, total => total.CategoryId is null).Total);

        var deletedSql = products.Query()
            .Where(product => product.Id == first.Id)
            .AsQuery()
            .ToCommand()
            .Sql;
        Assert.Contains("\"DeletedAt\" IS NULL", deletedSql);

        await products.DeleteAsync(first.Id);
        var remaining = await products.Query()
            .Where(product => product.Id == first.Id)
            .ToListAsync();
        Assert.Empty(remaining);

        var updated = await products.Query()
            .Where(product => product.Id == second.Id)
            .ExecuteUpdateAsync(columns => [columns.Name.Set("B updated")]);
        Assert.Equal(1, updated);
        Assert.Equal("B updated", (await products.GetByIdAsync(second.Id))!.Name);

        var hardDelete = new TypedQueryHardDeleteTypedRepository(session);
        await hardDelete.InsertAsync(new TypedQueryHardDelete { Id = 1, Name = "remove" });
        Assert.Equal(
            1,
            await hardDelete.Query()
                .Where(row => row.Name == "remove")
                .ExecuteDeleteAsync());
        Assert.Null(await hardDelete.GetByIdAsync(1));
    }

    [Theory]
    [InlineData(DatabaseKind.Sqlite, "DELETE FROM \"typed_query_hard_delete\" AS \"entity\" WHERE")]
    [InlineData(DatabaseKind.PostgreSql, "DELETE FROM \"typed_query_hard_delete\" AS \"entity\" WHERE")]
    [InlineData(DatabaseKind.SqlServer, "DELETE [entity] FROM [typed_query_hard_delete] AS [entity] WHERE")]
    [InlineData(DatabaseKind.MySql, "DELETE `entity` FROM `typed_query_hard_delete` AS `entity` WHERE")]
    [InlineData(DatabaseKind.Oracle, "DELETE FROM \"typed_query_hard_delete\" \"entity\" WHERE")]
    public void HardDeleteCommandIncludesTargetTableForEveryDialect(
        DatabaseKind kind,
        string expectedPrefix)
    {
        var session = new DataSession(
            () => throw new InvalidOperationException("Command rendering must not open a connection."),
            new SqlDialect(kind));
        var repository = new TypedQueryHardDeleteTypedRepository(session);

        var sql = repository.Query()
            .Where(row => row.Id == 7)
            .AsQuery()
            .DeleteCommand()
            .Sql;

        Assert.StartsWith(expectedPrefix, sql);
    }

    [Theory]
    [InlineData(DatabaseKind.Sqlite, "SUM(\"entity\".\"Price\")")]
    [InlineData(DatabaseKind.PostgreSql, "SUM(\"entity\".\"Price\")")]
    [InlineData(DatabaseKind.SqlServer, "SUM([entity].[Price])")]
    [InlineData(DatabaseKind.MySql, "SUM(`entity`.`Price`)")]
    [InlineData(DatabaseKind.Oracle, "SUM(\"entity\".\"Price\")")]
    public void AggregateRendersOnceUsingDialectIdentifiers(DatabaseKind kind, string expectedAggregate)
    {
        var session = new DataSession(
            () => throw new InvalidOperationException("Command rendering must not open a connection."),
            new SqlDialect(kind));
        var repository = new TypedQueryProductTypedRepository(session);

        var command = repository.Query()
            .Select(columns => new Projection<decimal?>(
                [Sql.Sum(columns.Price)],
                reader => DbValues.Read<decimal?>(reader, 0)))
            .ToCommand();

        Assert.Contains(expectedAggregate, command.Sql);
        var count = repository.Query()
            .Select(_ => new Projection<long>(
                [Sql.Count()],
                reader => DbValues.Read<long>(reader, 0)))
            .ToCommand();
        Assert.Contains(kind == DatabaseKind.SqlServer ? "COUNT_BIG(*)" : "COUNT(*)", count.Sql);
    }

    [Theory]
    [InlineData(DatabaseKind.Sqlite, "\"entity\".\"Price\"", "@p0", "LIMIT @p1 OFFSET @p2")]
    [InlineData(DatabaseKind.PostgreSql, "\"entity\".\"Price\"", "@p0", "LIMIT @p1 OFFSET @p2")]
    [InlineData(DatabaseKind.SqlServer, "[entity].[Price]", "@p0", "OFFSET @p1 ROWS FETCH NEXT @p2 ROWS ONLY")]
    [InlineData(DatabaseKind.MySql, "`entity`.`Price`", "@p0", "LIMIT @p1 OFFSET @p2")]
    [InlineData(DatabaseKind.Oracle, "\"entity\".\"Price\"", ":p0", "OFFSET :p1 ROWS FETCH NEXT :p2 ROWS ONLY")]
    public void FilteredPagingUsesDialectIdentifiersAndParameters(
        DatabaseKind kind,
        string expectedColumn,
        string expectedParameter,
        string expectedPaging)
    {
        var session = new DataSession(
            () => throw new InvalidOperationException("Command rendering must not open a connection."),
            new SqlDialect(kind));
        var repository = new TypedQueryProductTypedRepository(session);

        var command = repository.Query()
            .Where(product => product.Price >= 10m)
            .OrderBy(product => product.Id)
            .Skip(3)
            .Take(7)
            .ToCommand();

        Assert.Contains(expectedColumn, command.Sql);
        Assert.Contains(expectedParameter, command.Sql);
        Assert.Contains(expectedPaging, command.Sql);
        Assert.DoesNotContain("10", command.Sql);
        Assert.Equal(3, command.Arguments.Count);
        Assert.Equal(10m, command.Arguments[0].Value);
        Assert.Equal(kind is DatabaseKind.SqlServer or DatabaseKind.Oracle ? 3 : 7, command.Arguments[1].Value);
        Assert.Equal(kind is DatabaseKind.SqlServer or DatabaseKind.Oracle ? 7 : 3, command.Arguments[2].Value);
    }

    [Fact]
    public void EqualityUsesSimplePredicatesWhenNullSemanticsAllowIt()
    {
        var session = new DataSession(
            () => throw new InvalidOperationException("Command rendering must not open a connection."),
            new SqlDialect(DatabaseKind.Sqlite));
        var repository = new TypedQueryProductTypedRepository(session);

        var nonNullable = repository.Query()
            .Where(product => product.Name == "book")
            .ToCommand();
        Assert.Contains("\"entity\".\"Name\" = @p0", nonNullable.Sql);
        Assert.DoesNotContain("IS NOT NULL", nonNullable.Sql);

        var nullable = repository.Query()
            .Where(product => product.Notes != "archived")
            .ToCommand();
        Assert.Contains("\"entity\".\"Notes\" IS NULL OR", nullable.Sql);
    }

    [Fact]
    public void OracleInListsAreSplitWithoutApplyingTheInListLimitToTheWholeCommand()
    {
        var dialect = new SqlDialect(DatabaseKind.Oracle);
        var session = new DataSession(
            () => throw new InvalidOperationException("Command rendering must not open a connection."),
            dialect);
        var repository = new TypedQueryProductTypedRepository(session);

        var command = repository.Query()
            .Where(product => product.Id.In(Enumerable.Range(1, 1001)))
            .ToCommand();

        Assert.Equal(65535, dialect.MaxParameters);
        Assert.Equal(1000, dialect.MaxInItems);
        Assert.Equal(1001, command.Arguments.Count);
        Assert.Equal(2, command.Sql.Split(" IN (").Length - 1);
    }

    [Fact]
    public async Task WebApiQueryFeatures_StringSearch_In_Between_Paging_DeleteWhere()
    {
        var connectionString = $"Data Source=webapi-query-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync();
        await using (var command = anchor.CreateCommand())
        {
            command.CommandText = """
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
            await command.ExecuteNonQueryAsync();
        }

        await using var session = new DataSession(
            () => new SqliteConnection(connectionString),
            new SqlDialect(DatabaseKind.Sqlite));
        var repo = new TypedQueryProductTypedRepository(session);

        await repo.InsertRangeAsync([
            new TypedQueryProduct { Name = "Apple iPhone", Price = 999m },
            new TypedQueryProduct { Name = "Apple iPad", Price = 799m },
            new TypedQueryProduct { Name = "Samsung Galaxy", Price = 899m },
            new TypedQueryProduct { Name = "Google Pixel", Price = 699m },
            new TypedQueryProduct { Name = "Xiaomi 13", Price = 499m }
        ]);

        // 1. String Search: Contains
        var apples = await repo.Query()
            .Where(p => p.Name.Contains("Apple"))
            .ToListAsync();
        Assert.Equal(2, apples.Count);

        // 2. String Search: StartsWith
        var samsungs = await repo.Query()
            .Where(p => p.Name.StartsWith("Samsung"))
            .ToListAsync();
        Assert.Single(samsungs);

        // 3. String Search: EndsWith
        var pixels = await repo.Query()
            .Where(p => p.Name.EndsWith("Pixel"))
            .ToListAsync();
        Assert.Single(pixels);

        // 4. In(params) and NotIn
        var inList = await repo.Query()
            .Where(p => p.Price.In(499m, 999m))
            .ToListAsync();
        Assert.Equal(2, inList.Count);

        var notInList = await repo.Query()
            .Where(p => p.Price.NotIn(499m, 999m))
            .ToListAsync();
        Assert.Equal(3, notInList.Count);

        // 5. Between
        var midRange = await repo.Query()
            .Where(p => p.Price.Between(700m, 900m))
            .OrderBy(p => p.Price)
            .ToListAsync();
        Assert.Equal(2, midRange.Count);
        Assert.Equal(["Apple iPad", "Samsung Galaxy"], midRange.Select(p => p.Name));

        // 6. Pagination (ToPagedListAsync)
        var page = await repo.Query()
            .OrderBy(p => p.Price)
            .ToPagedListAsync(pageNumber: 2, pageSize: 2);
        Assert.Equal(5, page.TotalCount);
        Assert.Equal(2, page.PageNumber);
        Assert.Equal(2, page.PageSize);
        Assert.Equal(3, page.TotalPages);
        Assert.True(page.HasPreviousPage);
        Assert.True(page.HasNextPage);
        Assert.Equal(2, page.Items.Count);

        // 7. Dynamic WhereIf
        string? searchKeyword = "Galaxy";
        decimal? minFilterPrice = null;
        var dynamicResult = await repo.Query()
            .WhereIf(!string.IsNullOrWhiteSpace(searchKeyword), p => p.Name.Contains(searchKeyword!))
            .WhereIf(minFilterPrice.HasValue, p => p.Price >= minFilterPrice!.Value)
            .ToListAsync();
        Assert.Single(dynamicResult);
        Assert.Equal("Samsung Galaxy", dynamicResult[0].Name);

        // 8. DeleteWhereAsync (Set-based delete)
        var deletedCount = await repo.DeleteWhereAsync(p => p.Name.StartsWith("Apple"));
        Assert.Equal(2, deletedCount);

        var remainingCount = await repo.CountAsync();
        Assert.Equal(3, remainingCount);
    }
}

[Table("typed_query_categories")]
public sealed class TypedQueryCategory
{
    [PrimaryKey(AutoIncrement = true)]
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

[Table("typed_query_products")]
[SoftDelete("DeletedAt")]
public sealed class TypedQueryProduct
{
    [PrimaryKey(AutoIncrement = true)]
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;
    public int? CategoryId { get; set; }
    public decimal Price { get; set; }
    [JsonColumn(typeof(TypedQueryJsonContext))]
    public List<string>? Tags { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

[Table("typed_query_hard_delete")]
public sealed class TypedQueryHardDelete
{
    [PrimaryKey]
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed record TypedQueryProductCategory(string Product, string Category);
public sealed record TypedQueryCategoryTotal(int? CategoryId, decimal? Total);
public sealed record TypedQueryCategoryTotalWithName(string? CategoryName, long ProductCount, decimal? Total);

[JsonSerializable(typeof(List<string>))]
public partial class TypedQueryJsonContext : JsonSerializerContext;
