using Xunit;
using LinKit.Data;
using LinKit.Data.Linq;
using Microsoft.Extensions.DependencyInjection;

namespace LinKit.Messaging.Tests;

public sealed class DataGeneratorPartialTests
{
    [Theory]
    [InlineData(DatabaseDialect.Sqlite, "\"Price\"", "@")]
    [InlineData(DatabaseDialect.PostgreSql, "\"Price\"", "@")]
    [InlineData(DatabaseDialect.SqlServer, "[Price]", "@")]
    [InlineData(DatabaseDialect.MySql, "`Price`", "@")]
    [InlineData(DatabaseDialect.Oracle, "\"Price\"", ":")]
    public void ExpressionPredicatesUseDialectSpecificIdentifiersAndParameters(
        DatabaseDialect dialect,
        string quotedColumn,
        string parameterMarker)
    {
        var minimumPrice = 12.5m;
        var translator = new ExpressionSqlTranslator(
            dialect,
            new Dictionary<string, string> { [nameof(PriceFilter.Price)] = "Price" });

        var translated = translator.TranslatePredicate(
            (System.Linq.Expressions.Expression<Func<PriceFilter, bool>>)
                (product => product.Price >= minimumPrice));

        Assert.Contains(quotedColumn, translated.WhereSql);
        Assert.Contains($"{parameterMarker}p0", translated.WhereSql);
        Assert.Equal(minimumPrice, translated.Parameters["p0"]);
    }

    [Theory]
    [InlineData(DatabaseDialect.Sqlite, "\"Price\"", "@")]
    [InlineData(DatabaseDialect.PostgreSql, "\"Price\"", "@")]
    [InlineData(DatabaseDialect.SqlServer, "[Price]", "@")]
    [InlineData(DatabaseDialect.MySql, "`Price`", "@")]
    [InlineData(DatabaseDialect.Oracle, "\"Price\"", ":")]
    public void CombinedExpressionPredicatesRenderAsAstWithUniqueDialectParameters(
        DatabaseDialect dialect,
        string quotedColumn,
        string parameterMarker)
    {
        var minimumPrice = 10m;
        var maximumPrice = 50m;
        var translator = new ExpressionSqlTranslator(
            dialect,
            new Dictionary<string, string> { [nameof(PriceFilter.Price)] = "Price" });

        var translated = translator.TranslatePredicate(
            (System.Linq.Expressions.Expression<Func<PriceFilter, bool>>)
                (product => product.Price >= minimumPrice && product.Price < maximumPrice));

        Assert.Contains("AND", translated.WhereSql);
        Assert.Contains($"{quotedColumn} >=", translated.WhereSql);
        Assert.Contains($"{quotedColumn} <", translated.WhereSql);
        Assert.Contains($"{parameterMarker}p0", translated.WhereSql);
        Assert.Contains($"{parameterMarker}p1", translated.WhereSql);
        Assert.Equal(minimumPrice, translated.Parameters["p0"]);
        Assert.Equal(maximumPrice, translated.Parameters["p1"]);
    }

    [Theory]
    [InlineData(DatabaseDialect.Sqlite, "@")]
    [InlineData(DatabaseDialect.PostgreSql, "@")]
    [InlineData(DatabaseDialect.SqlServer, "@")]
    [InlineData(DatabaseDialect.MySql, "@")]
    [InlineData(DatabaseDialect.Oracle, ":")]
    public void StringPredicatesEscapeLikeWildcardsAndRemainParameterized(
        DatabaseDialect dialect,
        string parameterMarker)
    {
        const string searchTerm = "50%_!";
        var translator = new ExpressionSqlTranslator(
            dialect,
            new Dictionary<string, string> { [nameof(PriceFilter.Name)] = "Name" });

        var translated = translator.TranslatePredicate(
            (System.Linq.Expressions.Expression<Func<PriceFilter, bool>>)
                (product => product.Name.Contains(searchTerm)));

        Assert.Contains("ESCAPE '!'", translated.WhereSql);
        Assert.Contains($"{parameterMarker}p0", translated.WhereSql);
        Assert.Equal("%50!%!_!!%", translated.Parameters["p0"]);
    }

    [Theory]
    [InlineData(DatabaseDialect.Sqlite, "\"Price\" DESC")]
    [InlineData(DatabaseDialect.PostgreSql, "\"Price\" DESC")]
    [InlineData(DatabaseDialect.SqlServer, "[Price] DESC")]
    [InlineData(DatabaseDialect.MySql, "`Price` DESC")]
    [InlineData(DatabaseDialect.Oracle, "\"Price\" DESC")]
    public void ExpressionOrderingUsesDialectSpecificIdentifiers(
        DatabaseDialect dialect,
        string expectedOrder)
    {
        var translator = new ExpressionSqlTranslator(
            dialect,
            new Dictionary<string, string> { [nameof(PriceFilter.Price)] = "Price" });

        Assert.Equal(
            expectedOrder,
            translator.TranslateOrderBy(
                (System.Linq.Expressions.Expression<Func<PriceFilter, decimal>>)
                    (product => product.Price),
                descending: true));
    }

    [Fact]
    public async Task GeneratedRepositoryContractAndDiCanBeExtendedWithPartialDeclarations()
    {
        Assert.Equal("extended", KeyIntIdentityRepository.CustomMarker);
        Assert.True(typeof(IKeyIntIdentityRepository).IsAssignableFrom(typeof(KeyIntIdentityRepository)));

        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddLinKitData(options =>
        {
            options.UseConnection(
                DatabaseDialect.Sqlite,
                () => new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:"));
            options.EnsureDatabaseCreated = false;
            options.AutoSyncSchema = false;
            options.RunMigrations = false;
        });
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IKeyIntIdentityRepository>();

        Assert.Equal(42, await repository.CustomOperationAsync());
    }

    private sealed class PriceFilter
    {
        public decimal Price { get; init; }
        public string Name { get; init; } = string.Empty;
    }
}
