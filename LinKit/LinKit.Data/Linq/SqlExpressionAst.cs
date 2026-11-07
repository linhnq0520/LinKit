using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using LinKit.Data.Dialects;

namespace LinKit.Data.Linq;

internal abstract record SqlExpressionNode;

internal sealed record SqlColumnNode(string Name) : SqlExpressionNode;

internal sealed record SqlParameterNode(object? Value) : SqlExpressionNode;

internal sealed record SqlBinaryNode(
    SqlExpressionNode Left,
    string Operator,
    SqlExpressionNode Right) : SqlExpressionNode;

internal sealed record SqlLogicalNode(
    SqlExpressionNode Left,
    string Operator,
    SqlExpressionNode Right) : SqlExpressionNode;

internal sealed record SqlNotNode(SqlExpressionNode Operand) : SqlExpressionNode;

internal sealed record SqlNullCheckNode(SqlExpressionNode Operand, bool IsNotNull) : SqlExpressionNode;

internal sealed record SqlLikeNode(SqlExpressionNode Operand, SqlExpressionNode Pattern) : SqlExpressionNode;

internal sealed record SqlInNode(SqlExpressionNode Operand, IReadOnlyList<SqlExpressionNode> Values) : SqlExpressionNode;

internal sealed record SqlBooleanConstantNode(bool Value) : SqlExpressionNode;

internal sealed record SqlFunctionCallNode(string FunctionName, IReadOnlyList<SqlExpressionNode> Arguments) : SqlExpressionNode;

internal sealed record SqlOrderByNode(SqlExpressionNode Expression, bool IsDescending);

internal sealed class SqlExpressionRenderer
{
    private readonly DatabaseDialect _dialectKind;
    private readonly ISqlDialect _dialect;
    private readonly Dictionary<string, object?> _parameters = new(StringComparer.OrdinalIgnoreCase);
    private int _parameterIndex;

    public SqlExpressionRenderer(DatabaseDialect dialect, int parameterOffset = 0)
    {
        _dialectKind = dialect;
        _dialect = SqlDialectFactory.GetDialect(dialect);
        _parameterIndex = parameterOffset;
    }

    public Dictionary<string, object?> Parameters => _parameters;

    public string Render(SqlExpressionNode node) => node switch
    {
        SqlColumnNode column => _dialect.QuoteIdentifier(column.Name),
        SqlParameterNode parameter => RenderParameter(parameter.Value),
        SqlBinaryNode binary => $"({Render(binary.Left)} {binary.Operator} {Render(binary.Right)})",
        SqlLogicalNode logical => $"({Render(logical.Left)} {logical.Operator} {Render(logical.Right)})",
        SqlNotNode not => $"(NOT ({Render(not.Operand)}))",
        SqlNullCheckNode nullCheck => $"({Render(nullCheck.Operand)} IS {(nullCheck.IsNotNull ? "NOT " : string.Empty)}NULL)",
        SqlLikeNode like => $"({Render(like.Operand)} LIKE {Render(like.Pattern)} ESCAPE '!')",
        SqlInNode inNode when inNode.Values.Count == 0 => "(1 = 0)",
        SqlInNode inNode => RenderIn(inNode),
        SqlBooleanConstantNode constant => constant.Value ? "(1 = 1)" : "(1 = 0)",
        SqlFunctionCallNode func => $"{func.FunctionName}({string.Join(", ", func.Arguments.Select(Render))})",
        _ => throw new NotSupportedException($"SQL expression node '{node.GetType().Name}' is not supported.")
    };

    private string RenderIn(SqlInNode inNode)
    {
        var operand = Render(inNode.Operand);
        // Oracle has a hard limit of 1000 items in an IN clause (ORA-01795).
        // For Oracle, chunk into groups of 1000 connected with OR.
        if (_dialect.DialectType == DatabaseDialect.Oracle && inNode.Values.Count > 1000)
        {
            var chunkList = new List<string>();
            for (var i = 0; i < inNode.Values.Count; i += 1000)
            {
                var takeCount = Math.Min(1000, inNode.Values.Count - i);
                var chunkItems = new List<string>(takeCount);
                for (var j = 0; j < takeCount; j++)
                {
                    chunkItems.Add(Render(inNode.Values[i + j]));
                }
                chunkList.Add($"({operand} IN ({string.Join(", ", chunkItems)}))");
            }
            return $"({string.Join(" OR ", chunkList)})";
        }

        return $"({operand} IN ({string.Join(", ", inNode.Values.Select(Render))}))";
    }

    public string Render(SqlOrderByNode node) =>
        $"{Render(node.Expression)} {(node.IsDescending ? "DESC" : "ASC")}";

    private string RenderParameter(object? value)
    {
        var name = $"p{_parameterIndex++}";
        _parameters.Add(name, value);
        return $"{_dialect.ParameterPrefix}{name}";
    }
}
