using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace LinKit.Data.Linq;

/// <summary>
/// Result of translating a LINQ expression tree to SQL query parts.
/// </summary>
public sealed class TranslatedSqlQuery
{
    /// <summary>Rendered <c>WHERE</c> clause without the keyword, or <see langword="null"/> when the expression has no filter.</summary>
    public string? WhereSql { get; init; }

    /// <summary>Named parameters referenced by <see cref="WhereSql"/>.</summary>
    public Dictionary<string, object?> Parameters { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Ordering terms in priority order; the column is already quoted for the dialect.</summary>
    public List<(string Column, bool IsDescending)> OrderBys { get; init; } = new();

    /// <summary>Requested row offset, or <see langword="null"/> when the query is not paginated.</summary>
    public int? Skip { get; init; }

    /// <summary>Requested row limit, or <see langword="null"/> when the query is not paginated.</summary>
    public int? Take { get; init; }
}

/// <summary>
/// Translates supported LINQ expressions into a provider-neutral SQL expression tree.
/// </summary>
public sealed class ExpressionSqlTranslator
{
    private readonly DatabaseDialect _dialect;
    private readonly IReadOnlyDictionary<string, string> _propertyToColumn;
    private readonly int _parameterOffset;

    /// <summary>Creates a translator.</summary>
    /// <param name="dialect">Dialect used to quote identifiers and render literals.</param>
    /// <param name="propertyToColumn">Maps CLR property names to physical column names.</param>
    /// <param name="parameterOffset">Initial parameter number, used to keep names unique across composed statements.</param>
    public ExpressionSqlTranslator(
        DatabaseDialect dialect,
        IReadOnlyDictionary<string, string> propertyToColumn,
        int parameterOffset = 0
    )
    {
        _dialect = dialect;
        _propertyToColumn = propertyToColumn;
        _parameterOffset = parameterOffset;
    }

    /// <summary>Translates a full query expression into its <c>WHERE</c>, ordering and pagination parts.</summary>
    /// <param name="expression">Expression tree of the query.</param>
    /// <returns>The translated parts.</returns>
    public TranslatedSqlQuery Translate(Expression expression) => Translate(expression, null);

    /// <summary>Translates a predicate lambda into a <c>WHERE</c> clause.</summary>
    /// <param name="predicate">Predicate with exactly one parameter.</param>
    /// <returns>The rendered clause and its parameters.</returns>
    /// <exception cref="NotSupportedException">The predicate uses a construct the translator does not support.</exception>
    public TranslatedSqlQuery TranslatePredicate(LambdaExpression predicate)
    {
        ValidateSingleParameter(predicate, nameof(predicate));
        var node = ParsePredicate(predicate.Body);
        var renderer = new SqlExpressionRenderer(_dialect, _parameterOffset);
        return new TranslatedSqlQuery
        {
            WhereSql = renderer.Render(node),
            Parameters = renderer.Parameters,
        };
    }

    /// <summary>Translates a key selector into an <c>ORDER BY</c> term.</summary>
    /// <param name="keySelector">Selector naming a single mapped property.</param>
    /// <param name="descending">Whether the term orders descending.</param>
    /// <returns>The rendered ordering term.</returns>
    /// <exception cref="NotSupportedException">The selector is not a single mapped property.</exception>
    public string TranslateOrderBy(LambdaExpression keySelector, bool descending = false)
    {
        ValidateSingleParameter(keySelector, nameof(keySelector));
        var propertyName = ResolveEntityPropertyName(keySelector.Body);
        if (!_propertyToColumn.TryGetValue(propertyName, out var columnName))
        {
            throw new NotSupportedException(
                $"Property '{propertyName}' is not mapped to a database column."
            );
        }

        var renderer = new SqlExpressionRenderer(_dialect);
        return renderer.Render(new SqlOrderByNode(new SqlColumnNode(columnName), descending));
    }

    /// <summary>Translates a full query expression, optionally adding an extra filter.</summary>
    /// <param name="expression">Expression tree of the query.</param>
    /// <param name="additionalWhere">Extra predicate combined with <c>AND</c>, for example the tenant filter.</param>
    /// <returns>The translated parts.</returns>
    public TranslatedSqlQuery Translate(Expression expression, Expression? additionalWhere)
    {
        var wheres = new List<SqlExpressionNode>();
        var orderBys = new List<(string Column, bool IsDescending)>();
        int? skip = null;
        int? take = null;

        var current = expression;
        var callStack = new Stack<MethodCallExpression>();
        while (current is MethodCallExpression call)
        {
            callStack.Push(call);
            current = call.Arguments[0];
        }

        while (callStack.Count > 0)
        {
            var call = callStack.Pop();
            switch (call.Method.Name)
            {
                case "Where":
                    wheres.Add(ParsePredicate(UnquoteLambda(call.Arguments[1]).Body));
                    break;
                case "OrderBy":
                    orderBys.Clear();
                    orderBys.Add(
                        (ResolveOrderColumn(UnquoteLambda(call.Arguments[1]).Body), false)
                    );
                    break;
                case "OrderByDescending":
                    orderBys.Clear();
                    orderBys.Add((ResolveOrderColumn(UnquoteLambda(call.Arguments[1]).Body), true));
                    break;
                case "ThenBy":
                    orderBys.Add(
                        (ResolveOrderColumn(UnquoteLambda(call.Arguments[1]).Body), false)
                    );
                    break;
                case "ThenByDescending":
                    orderBys.Add((ResolveOrderColumn(UnquoteLambda(call.Arguments[1]).Body), true));
                    break;
                case "Skip":
                {
                    var count = Math.Max(0, Convert.ToInt32(ExtractValue(call.Arguments[1])));
                    skip = (int)Math.Min(int.MaxValue, (long)(skip ?? 0) + count);
                    if (take.HasValue)
                        take = Math.Max(0, take.Value - count);
                    break;
                }
                case "Take":
                {
                    var count = Math.Max(0, Convert.ToInt32(ExtractValue(call.Arguments[1])));
                    take = Math.Min(take ?? int.MaxValue, count);
                    break;
                }
                case "Select":
                    break;
                default:
                    throw new NotSupportedException(
                        $"LINQ operator '{call.Method.Name}' is not supported."
                    );
            }
        }

        if (additionalWhere is not null)
        {
            wheres.Add(ParsePredicate(UnquoteLambda(additionalWhere).Body));
        }

        SqlExpressionNode? whereNode = null;
        foreach (var where in wheres)
        {
            whereNode = whereNode is null ? where : new SqlLogicalNode(whereNode, "AND", where);
        }

        var translated = new TranslatedSqlQuery
        {
            OrderBys = orderBys,
            Skip = skip,
            Take = take,
        };
        if (whereNode is not null)
        {
            var renderer = new SqlExpressionRenderer(_dialect, _parameterOffset);
            translated = new TranslatedSqlQuery
            {
                WhereSql = renderer.Render(whereNode),
                Parameters = renderer.Parameters,
                OrderBys = orderBys,
                Skip = skip,
                Take = take,
            };
        }

        return translated;
    }

    private SqlExpressionNode ParsePredicate(Expression expression)
    {
        expression = StripConvert(expression);
        if (expression is BinaryExpression binary)
        {
            return binary.NodeType switch
            {
                ExpressionType.AndAlso => new SqlLogicalNode(
                    ParsePredicate(binary.Left),
                    "AND",
                    ParsePredicate(binary.Right)
                ),
                ExpressionType.OrElse => new SqlLogicalNode(
                    ParsePredicate(binary.Left),
                    "OR",
                    ParsePredicate(binary.Right)
                ),
                ExpressionType.Equal => ParseEquality(binary.Left, binary.Right, isNotEqual: false),
                ExpressionType.NotEqual => ParseEquality(
                    binary.Left,
                    binary.Right,
                    isNotEqual: true
                ),
                ExpressionType.GreaterThan => new SqlBinaryNode(
                    ParseValue(binary.Left),
                    ">",
                    ParseValue(binary.Right)
                ),
                ExpressionType.GreaterThanOrEqual => new SqlBinaryNode(
                    ParseValue(binary.Left),
                    ">=",
                    ParseValue(binary.Right)
                ),
                ExpressionType.LessThan => new SqlBinaryNode(
                    ParseValue(binary.Left),
                    "<",
                    ParseValue(binary.Right)
                ),
                ExpressionType.LessThanOrEqual => new SqlBinaryNode(
                    ParseValue(binary.Left),
                    "<=",
                    ParseValue(binary.Right)
                ),
                _ => throw UnsupportedExpression(expression),
            };
        }

        if (expression is UnaryExpression unary && unary.NodeType == ExpressionType.Not)
        {
            var operand = StripConvert(unary.Operand);
            if (
                operand is MemberExpression member
                && IsEntityMember(member)
                && IsBoolean(member.Type)
            )
            {
                return new SqlBinaryNode(
                    ParseEntityMember(member),
                    "=",
                    new SqlParameterNode(false)
                );
            }

            return new SqlNotNode(ParsePredicate(operand));
        }

        if (
            expression is MemberExpression booleanMember
            && IsEntityMember(booleanMember)
            && IsBoolean(booleanMember.Type)
        )
        {
            return new SqlBinaryNode(
                ParseEntityMember(booleanMember),
                "=",
                new SqlParameterNode(true)
            );
        }

        if (expression is MethodCallExpression methodCall)
        {
            return ParseMethodCall(methodCall);
        }

        throw UnsupportedExpression(expression);
    }

    private SqlExpressionNode ParseValue(Expression expression)
    {
        expression = StripConvert(expression);

        if (expression is MemberExpression member)
        {
            return IsEntityMember(member)
                ? ParseEntityMember(member)
                : new SqlParameterNode(ExtractValue(member));
        }

        if (expression is ConstantExpression constant)
        {
            return new SqlParameterNode(constant.Value);
        }

        if (expression is MethodCallExpression methodCall)
        {
            return ParseMethodCall(methodCall);
        }

        throw UnsupportedExpression(expression);
    }

    private SqlExpressionNode ParseEquality(Expression left, Expression right, bool isNotEqual)
    {
        var leftIsNull = IsNullValue(left);
        var rightIsNull = IsNullValue(right);
        if (leftIsNull && rightIsNull)
        {
            return new SqlBooleanConstantNode(!isNotEqual);
        }

        if (leftIsNull)
        {
            return new SqlNullCheckNode(ParseValue(right), isNotEqual);
        }

        if (rightIsNull)
        {
            return new SqlNullCheckNode(ParseValue(left), isNotEqual);
        }

        var leftNode = ParseValue(left);
        var rightNode = ParseValue(right);
        var leftNullable = CanBeNull(left);
        var rightNullable = CanBeNull(right);

        // C# null semantics differ from SQL UNKNOWN: null != value is true in C# but the row
        // would be dropped by a plain <>; null == null is true in C# but UNKNOWN in SQL.
        if (isNotEqual)
        {
            if (leftNullable && rightNullable)
            {
                return new SqlLogicalNode(
                    new SqlBinaryNode(leftNode, "<>", rightNode),
                    "OR",
                    new SqlLogicalNode(
                        new SqlLogicalNode(
                            new SqlNullCheckNode(leftNode, false),
                            "AND",
                            new SqlNullCheckNode(rightNode, true)
                        ),
                        "OR",
                        new SqlLogicalNode(
                            new SqlNullCheckNode(leftNode, true),
                            "AND",
                            new SqlNullCheckNode(rightNode, false)
                        )
                    )
                );
            }

            if (leftNullable)
            {
                return new SqlLogicalNode(
                    new SqlBinaryNode(leftNode, "<>", rightNode),
                    "OR",
                    new SqlNullCheckNode(leftNode, false)
                );
            }

            if (rightNullable)
            {
                return new SqlLogicalNode(
                    new SqlBinaryNode(leftNode, "<>", rightNode),
                    "OR",
                    new SqlNullCheckNode(rightNode, false)
                );
            }
        }
        else if (leftNullable && rightNullable)
        {
            return new SqlLogicalNode(
                new SqlBinaryNode(leftNode, "=", rightNode),
                "OR",
                new SqlLogicalNode(
                    new SqlNullCheckNode(leftNode, false),
                    "AND",
                    new SqlNullCheckNode(rightNode, false)
                )
            );
        }

        return new SqlBinaryNode(leftNode, isNotEqual ? "<>" : "=", rightNode);
    }

    private static bool CanBeNull(Expression expression)
    {
        expression = StripConvert(expression);
        if (expression is MemberExpression member && IsEntityMember(member))
        {
            return !member.Type.IsValueType || Nullable.GetUnderlyingType(member.Type) is not null;
        }

        if (
            expression is MethodCallExpression call
            && call.Method.DeclaringType == typeof(string)
            && call.Object is not null
            && call.Method.Name is "ToLower" or "ToUpper" or "Trim"
        )
        {
            return CanBeNull(call.Object);
        }

        // Captured values are evaluated during translation; nulls are handled by IsNullValue.
        return false;
    }

    private SqlExpressionNode ParseMethodCall(MethodCallExpression call)
    {
        if (
            call.Method.DeclaringType == typeof(string)
            && call.Object is not null
            && call.Arguments.Count == 1
            && call.Method.Name is "Contains" or "StartsWith" or "EndsWith"
        )
        {
            var pattern = ExtractValue(call.Arguments[0])?.ToString() ?? string.Empty;
            pattern = EscapeLikePattern(pattern, _dialect);
            pattern = call.Method.Name switch
            {
                "Contains" => $"%{pattern}%",
                "StartsWith" => $"{pattern}%",
                _ => $"%{pattern}",
            };
            return new SqlLikeNode(ParseValue(call.Object), new SqlParameterNode(pattern));
        }

        if (call.Method.DeclaringType == typeof(string) && call.Object is not null)
        {
            if (call.Arguments.Count == 0 && call.Method.Name is "ToLower" or "ToLowerInvariant")
            {
                return new SqlFunctionCallNode("LOWER", new[] { ParseValue(call.Object) });
            }

            if (call.Arguments.Count == 0 && call.Method.Name is "ToUpper" or "ToUpperInvariant")
            {
                return new SqlFunctionCallNode("UPPER", new[] { ParseValue(call.Object) });
            }

            if (call.Arguments.Count == 0 && call.Method.Name is "Trim")
            {
                return new SqlFunctionCallNode("TRIM", new[] { ParseValue(call.Object) });
            }
        }

        if (call.Method.Name == "Contains")
        {
            Expression? collectionExpression = null;
            Expression? itemExpression = null;
            if (call.Method.DeclaringType == typeof(Enumerable) && call.Arguments.Count == 2)
            {
                collectionExpression = call.Arguments[0];
                itemExpression = call.Arguments[1];
            }
            else if (call.Object is not null && call.Arguments.Count == 1)
            {
                collectionExpression = call.Object;
                itemExpression = call.Arguments[0];
            }

            if (collectionExpression is not null && itemExpression is not null)
            {
                var values = ExtractValue(collectionExpression);
                if (values is IEnumerable enumerable)
                {
                    var parameters = new List<SqlExpressionNode>();
                    foreach (var value in enumerable)
                    {
                        parameters.Add(new SqlParameterNode(value));
                    }

                    return new SqlInNode(ParseValue(itemExpression), parameters);
                }
            }
        }

        if (call.Method.Name == nameof(object.Equals))
        {
            if (call.Object is not null && call.Arguments.Count == 1)
            {
                return ParseEquality(call.Object, call.Arguments[0], isNotEqual: false);
            }

            if (call.Arguments.Count == 2)
            {
                return ParseEquality(call.Arguments[0], call.Arguments[1], isNotEqual: false);
            }
        }

        throw new NotSupportedException(
            $"LINQ method call '{call.Method.DeclaringType?.Name}.{call.Method.Name}' is not supported."
        );
    }

    private SqlColumnNode ParseEntityMember(MemberExpression member)
    {
        var propertyName = member.Member.Name;
        if (!_propertyToColumn.TryGetValue(propertyName, out var columnName))
        {
            throw new NotSupportedException(
                $"Property '{propertyName}' is not mapped to a database column."
            );
        }

        return new SqlColumnNode(columnName);
    }

    private string ResolveOrderColumn(Expression expression)
    {
        var propertyName = ResolveEntityPropertyName(expression);
        if (!_propertyToColumn.TryGetValue(propertyName, out var columnName))
        {
            throw new NotSupportedException(
                $"Property '{propertyName}' is not mapped to a database column."
            );
        }

        return columnName;
    }

    private static string ResolveEntityPropertyName(Expression expression)
    {
        expression = StripConvert(expression);
        if (expression is MemberExpression member && IsEntityMember(member))
        {
            return member.Member.Name;
        }

        throw new NotSupportedException("Expression must select a mapped entity property.");
    }

    private static bool IsEntityMember(MemberExpression member)
    {
        Expression? root = member.Expression;
        while (root is MemberExpression nested)
        {
            root = nested.Expression;
        }

        return root is ParameterExpression;
    }

    private static bool IsBoolean(Type type) => type == typeof(bool) || type == typeof(bool?);

    private static bool IsNullConstant(Expression expression)
    {
        expression = StripConvert(expression);
        return expression is ConstantExpression { Value: null };
    }

    private bool IsNullValue(Expression expression)
    {
        if (IsNullConstant(expression))
        {
            return true;
        }

        expression = StripConvert(expression);
        return expression is MemberExpression member
            && !IsEntityMember(member)
            && ExtractValue(member) is null;
    }

    private static string EscapeLikePattern(string value, DatabaseDialect dialect)
    {
        var escaped = value
            .Replace("!", "!!", StringComparison.Ordinal)
            .Replace("%", "!%", StringComparison.Ordinal)
            .Replace("_", "!_", StringComparison.Ordinal);
        return dialect == DatabaseDialect.SqlServer
            ? escaped.Replace("[", "![", StringComparison.Ordinal)
            : escaped;
    }

    private static Expression StripConvert(Expression expression)
    {
        while (
            expression is UnaryExpression unary
            && unary.NodeType
                is ExpressionType.Convert
                    or ExpressionType.ConvertChecked
                    or ExpressionType.Quote
        )
        {
            expression = unary.Operand;
        }

        return expression;
    }

    private static LambdaExpression UnquoteLambda(Expression expression)
    {
        expression = StripConvert(expression);
        return expression as LambdaExpression
            ?? throw new NotSupportedException("LINQ operator requires a lambda expression.");
    }

    private static void ValidateSingleParameter(LambdaExpression expression, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(expression, parameterName);
        if (expression.Parameters.Count != 1)
        {
            throw new ArgumentException(
                "Expression must have exactly one entity parameter.",
                parameterName
            );
        }
    }

    private static NotSupportedException UnsupportedExpression(Expression expression) =>
        new($"LINQ expression node '{expression.NodeType}' ({expression}) is not supported.");

    private static object? ExtractValue(Expression? expression)
    {
        if (expression is null)
        {
            return null;
        }

        expression = StripConvert(expression);
        if (expression is ConstantExpression constant)
        {
            return constant.Value;
        }

        if (expression is MemberExpression member && !IsEntityMember(member))
        {
            var container = ExtractValue(member.Expression);
            return member.Member switch
            {
                FieldInfo field => field.GetValue(container),
                PropertyInfo property => property.GetValue(container),
                _ => throw UnsupportedExpression(expression),
            };
        }

        throw UnsupportedExpression(expression);
    }
}
