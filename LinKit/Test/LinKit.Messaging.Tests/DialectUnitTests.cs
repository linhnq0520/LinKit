using LinKit.Data;
using LinKit.Data.Dialects;
using LinKit.Data.Linq;
using Xunit;

namespace LinKit.Messaging.Tests;

public sealed class DialectUnitTests
{
    [Fact]
    public void OraclePaging_WithoutOrderBy_AppendsFallbackOrder()
    {
        var oracleDialect = new SqlDialect(DatabaseKind.Oracle);
        var sql = "SELECT * FROM Users";
        var paginated = oracleDialect.Paginate(sql, order: null, skip: 0, take: 10);

        Assert.Contains("ORDER BY (SELECT NULL)", paginated);
        Assert.Contains("OFFSET 0 ROWS FETCH NEXT 10 ROWS ONLY", paginated);
    }

    [Fact]
    public void OraclePaging_WithOrderBy_PreservesCustomOrder()
    {
        var oracleDialect = new SqlDialect(DatabaseKind.Oracle);
        var sql = "SELECT * FROM Users";
        var paginated = oracleDialect.Paginate(sql, order: "Id DESC", skip: 20, take: 10);

        Assert.Contains("ORDER BY Id DESC", paginated);
        Assert.DoesNotContain("(SELECT NULL)", paginated);
        Assert.Contains("OFFSET 20 ROWS FETCH NEXT 10 ROWS ONLY", paginated);
    }

    [Fact]
    public void LinqAst_OracleInNode_ChunksOver1000Items()
    {
        var renderer = new SqlExpressionRenderer(DatabaseDialect.Oracle);
        var column = new SqlColumnNode("Id");
        var values = Enumerable.Range(1, 1500)
            .Select(i => (SqlExpressionNode)new SqlParameterNode(i))
            .ToList();

        var inNode = new SqlInNode(column, values);
        var rendered = renderer.Render(inNode);

        Assert.Equal("Oracle", DatabaseDialect.Oracle.ToString());
        Assert.Equal(1500, inNode.Values.Count);
        Assert.StartsWith("((", rendered);
        Assert.Contains(") OR (", rendered);
        Assert.Equal(1500, renderer.Parameters.Count);
    }

    [Fact]
    public void LinqAst_PostgreSqlInNode_DoesNotChunkOver1000Items()
    {
        var renderer = new SqlExpressionRenderer(DatabaseDialect.PostgreSql);
        var column = new SqlColumnNode("Id");
        var values = Enumerable.Range(1, 1500)
            .Select(i => (SqlExpressionNode)new SqlParameterNode(i))
            .ToList();

        var inNode = new SqlInNode(column, values);
        var rendered = renderer.Render(inNode);

        // Postgres supports arbitrary IN size, no OR chunking needed
        Assert.DoesNotContain(") OR (", rendered);
        Assert.StartsWith("(\"Id\" IN (", rendered);
        Assert.Equal(1500, renderer.Parameters.Count);
    }

        [Fact]
    public void Linq_StringFunctions_TranslateToLowerUpperTrim()
    {
        var mapping = new Dictionary<string, string> { ["Name"] = "Name" };
        var translator = new ExpressionSqlTranslator(DatabaseDialect.PostgreSql, mapping);
        
        System.Linq.Expressions.Expression<Func<TestDialectUserEntity, bool>> expr1 = u => u.Name.ToLower() == "john";
        var lowerPredicate = translator.TranslatePredicate(expr1);
        Assert.Contains("LOWER(\"Name\") = @p0", lowerPredicate.WhereSql);
        Assert.Equal("john", lowerPredicate.Parameters["p0"]);

        System.Linq.Expressions.Expression<Func<TestDialectUserEntity, bool>> expr2 = u => u.Name.ToUpper() == "JOHN";
        var upperPredicate = translator.TranslatePredicate(expr2);
        Assert.Contains("UPPER(\"Name\") = @p0", upperPredicate.WhereSql);

        System.Linq.Expressions.Expression<Func<TestDialectUserEntity, bool>> expr3 = u => u.Name.Trim() == "john";
        var trimPredicate = translator.TranslatePredicate(expr3);
        Assert.Contains("TRIM(\"Name\") = @p0", trimPredicate.WhereSql);
    }
}

public sealed class TestDialectUserEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}