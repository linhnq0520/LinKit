using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace LinKit.Data.Linq;

/// <summary>
/// Result of translating a LINQ expression tree to SQL query parts.
/// </summary>
public sealed class TranslatedSqlQuery
{
    public string? WhereSql { get; init; }
    public Dictionary<string, object?> Parameters { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public List<(string Column, bool IsDescending)> OrderBys { get; init; } = new();
    public int? Skip { get; init; }
    public int? Take { get; init; }
}

/// <summary>
/// AOT-friendly, lightweight AST expression visitor translating LINQ expressions to SQL query clauses.
/// </summary>
public sealed class ExpressionSqlTranslator
{
    private readonly DatabaseDialect _dialect;
    private readonly IReadOnlyDictionary<string, string> _propertyToColumn;
    private readonly Dictionary<string, object?> _parameters = new(StringComparer.OrdinalIgnoreCase);
    private int _paramCounter = 0;

    public ExpressionSqlTranslator(DatabaseDialect dialect, IReadOnlyDictionary<string, string> propertyToColumn)
    {
        _dialect = dialect;
        _propertyToColumn = propertyToColumn;
    }

    public TranslatedSqlQuery Translate(Expression expression)
    {
        return Translate(expression, null);
    }

    public TranslatedSqlQuery Translate(Expression expression, Expression? additionalWhere)
    {
        var wheres = new List<LambdaExpression>();
        var orderBys = new List<(LambdaExpression Selector, bool IsDescending)>();
        int? skip = null;
        int? take = null;

        // Unwind method call chain
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
            var name = call.Method.Name;
            switch (name)
            {
                case "Where":
                    wheres.Add(UnquoteLambda(call.Arguments[1]));
                    break;
                case "OrderBy":
                    orderBys.Clear();
                    orderBys.Add((UnquoteLambda(call.Arguments[1]), false));
                    break;
                case "OrderByDescending":
                    orderBys.Clear();
                    orderBys.Add((UnquoteLambda(call.Arguments[1]), true));
                    break;
                case "ThenBy":
                    orderBys.Add((UnquoteLambda(call.Arguments[1]), false));
                    break;
                case "ThenByDescending":
                    orderBys.Add((UnquoteLambda(call.Arguments[1]), true));
                    break;
                case "Skip":
                    skip = Convert.ToInt32(ExtractValue(call.Arguments[1]));
                    break;
                case "Take":
                    take = Convert.ToInt32(ExtractValue(call.Arguments[1]));
                    break;
                case "Select":
                    // Identity or projection; base query remains on entity table
                    break;
            }
        }

        if (additionalWhere != null)
        {
            wheres.Add(UnquoteLambda(additionalWhere));
        }

        // Translate WHERE clauses
        string? whereSql = null;
        if (wheres.Count > 0)
        {
            var parts = new List<string>();
            foreach (var w in wheres)
            {
                var clause = VisitExpression(w.Body);
                if (!string.IsNullOrWhiteSpace(clause))
                {
                    parts.Add(clause);
                }
            }
            if (parts.Count > 0)
            {
                whereSql = string.Join(" AND ", parts.Select(p => $"({p})"));
            }
        }

        // Translate ORDER BY clauses
        var translatedOrderBys = new List<(string Column, bool IsDescending)>();
        foreach (var (selector, isDesc) in orderBys)
        {
            var col = ResolveColumnName(selector.Body);
            if (!string.IsNullOrEmpty(col))
            {
                translatedOrderBys.Add((col, isDesc));
            }
        }

        return new TranslatedSqlQuery
        {
            WhereSql = whereSql,
            Parameters = _parameters,
            OrderBys = translatedOrderBys,
            Skip = skip,
            Take = take
        };
    }

    private static LambdaExpression UnquoteLambda(Expression expr)
    {
        while (expr is UnaryExpression u && (u.NodeType == ExpressionType.Quote || u.NodeType == ExpressionType.Convert))
        {
            expr = u.Operand;
        }
        return (LambdaExpression)expr;
    }

    private string QuoteColumn(string col) => _dialect switch
    {
        DatabaseDialect.SqlServer => $"[{col}]",
        DatabaseDialect.MySql => $"`{col}`",
        _ => $"\"{col}\""
    };

    private string ParamPlaceholder(string paramName) =>
        _dialect == DatabaseDialect.Oracle ? $":{paramName}" : $"@{paramName}";

    private string AddParameter(object? value)
    {
        var name = $"p{_paramCounter++}";
        _parameters[name] = value;
        return ParamPlaceholder(name);
    }

    private string? ResolveColumnName(Expression expr)
    {
        if (expr is UnaryExpression u && (u.NodeType == ExpressionType.Convert || u.NodeType == ExpressionType.ConvertChecked))
        {
            expr = u.Operand;
        }

        if (expr is MemberExpression m)
        {
            var propName = m.Member.Name;
            return _propertyToColumn.TryGetValue(propName, out var col) ? col : propName;
        }

        return null;
    }

    private string VisitExpression(Expression node)
    {
        // Strip converts
        if (node is UnaryExpression u && (u.NodeType == ExpressionType.Convert || u.NodeType == ExpressionType.ConvertChecked))
        {
            node = u.Operand;
        }

        // Binary: AndAlso / OrElse
        if (node is BinaryExpression b)
        {
            switch (b.NodeType)
            {
                case ExpressionType.AndAlso:
                    return $"({VisitExpression(b.Left)} AND {VisitExpression(b.Right)})";
                case ExpressionType.OrElse:
                    return $"({VisitExpression(b.Left)} OR {VisitExpression(b.Right)})";
                case ExpressionType.Equal:
                    return HandleEquality(b.Left, b.Right, isNotEqual: false);
                case ExpressionType.NotEqual:
                    return HandleEquality(b.Left, b.Right, isNotEqual: true);
                case ExpressionType.GreaterThan:
                    return $"({VisitExpression(b.Left)} > {VisitExpression(b.Right)})";
                case ExpressionType.GreaterThanOrEqual:
                    return $"({VisitExpression(b.Left)} >= {VisitExpression(b.Right)})";
                case ExpressionType.LessThan:
                    return $"({VisitExpression(b.Left)} < {VisitExpression(b.Right)})";
                case ExpressionType.LessThanOrEqual:
                    return $"({VisitExpression(b.Left)} <= {VisitExpression(b.Right)})";
            }
        }

        // Unary NOT: !p.IsFeatured
        if (node is UnaryExpression un && un.NodeType == ExpressionType.Not)
        {
            var operandCol = ResolveColumnName(un.Operand);
            if (operandCol != null)
            {
                return $"({QuoteColumn(operandCol)} = {AddParameter(false)})";
            }
            return $"(NOT ({VisitExpression(un.Operand)}))";
        }

        // Method call: string Contains, StartsWith, EndsWith, Enumerable.Contains
        if (node is MethodCallExpression call)
        {
            return HandleMethodCall(call);
        }

        // Member: p.IsFeatured (boolean standalone) or local variable closure
        if (node is MemberExpression m)
        {
            if (IsEntityMember(m))
            {
                var col = ResolveColumnName(m);
                if (col != null)
                {
                    if (m.Type == typeof(bool) || m.Type == typeof(bool?))
                    {
                        return $"({QuoteColumn(col)} = {AddParameter(true)})";
                    }
                    return QuoteColumn(col);
                }
            }
            else
            {
                var val = ExtractValue(m);
                return AddParameter(val);
            }
        }

        // Constant value
        if (node is ConstantExpression c)
        {
            return AddParameter(c.Value);
        }

        // Fallback: evaluate expression
        var fallbackVal = ExtractValue(node);
        return AddParameter(fallbackVal);
    }

    private string HandleEquality(Expression left, Expression right, bool isNotEqual)
    {
        bool leftIsNull = IsNullConstant(left);
        bool rightIsNull = IsNullConstant(right);

        if (leftIsNull && rightIsNull)
        {
            return isNotEqual ? "(1 = 0)" : "(1 = 1)";
        }

        if (rightIsNull)
        {
            return $"({VisitExpression(left)} {(isNotEqual ? "IS NOT NULL" : "IS NULL")})";
        }

        if (leftIsNull)
        {
            return $"({VisitExpression(right)} {(isNotEqual ? "IS NOT NULL" : "IS NULL")})";
        }

        var op = isNotEqual ? "<>" : "=";
        return $"({VisitExpression(left)} {op} {VisitExpression(right)})";
    }

    private static bool IsNullConstant(Expression expr)
    {
        if (expr is ConstantExpression c && c.Value == null) return true;
        if (expr is UnaryExpression u && (u.NodeType == ExpressionType.Convert || u.NodeType == ExpressionType.ConvertChecked))
        {
            return IsNullConstant(u.Operand);
        }
        return false;
    }

    private bool IsEntityMember(MemberExpression m)
    {
        var root = m.Expression;
        while (root is MemberExpression inner)
        {
            root = inner.Expression;
        }
        return root is ParameterExpression;
    }

    private string HandleMethodCall(MethodCallExpression call)
    {
        // string.Contains(substring)
        if (call.Method.DeclaringType == typeof(string) && call.Method.Name == "Contains" && call.Object != null && call.Arguments.Count == 1)
        {
            var col = ResolveColumnName(call.Object);
            if (col != null)
            {
                var val = ExtractValue(call.Arguments[0])?.ToString() ?? "";
                return $"({QuoteColumn(col)} LIKE {AddParameter($"%{val}%")})";
            }
        }

        // string.StartsWith(prefix)
        if (call.Method.DeclaringType == typeof(string) && call.Method.Name == "StartsWith" && call.Object != null && call.Arguments.Count == 1)
        {
            var col = ResolveColumnName(call.Object);
            if (col != null)
            {
                var val = ExtractValue(call.Arguments[0])?.ToString() ?? "";
                return $"({QuoteColumn(col)} LIKE {AddParameter($"{val}%")})";
            }
        }

        // string.EndsWith(suffix)
        if (call.Method.DeclaringType == typeof(string) && call.Method.Name == "EndsWith" && call.Object != null && call.Arguments.Count == 1)
        {
            var col = ResolveColumnName(call.Object);
            if (col != null)
            {
                var val = ExtractValue(call.Arguments[0])?.ToString() ?? "";
                return $"({QuoteColumn(col)} LIKE {AddParameter($"%{val}")})";
            }
        }

        // Enumerable.Contains(collection, p.Col)
        if (call.Method.Name == "Contains")
        {
            Expression? collectionExpr = null;
            Expression? itemExpr = null;

            if (call.Method.DeclaringType == typeof(Enumerable) && call.Arguments.Count == 2)
            {
                collectionExpr = call.Arguments[0];
                itemExpr = call.Arguments[1];
            }
            else if (call.Object != null && call.Arguments.Count == 1)
            {
                collectionExpr = call.Object;
                itemExpr = call.Arguments[0];
            }

            if (collectionExpr != null && itemExpr != null)
            {
                var col = ResolveColumnName(itemExpr);
                if (col != null)
                {
                    var collectionVal = ExtractValue(collectionExpr);
                    if (collectionVal is IEnumerable enumerable)
                    {
                        var items = new List<object?>();
                        foreach (var it in enumerable) items.Add(it);
                        if (items.Count == 0) return "(1 = 0)";

                        var placeholders = items.Select(it => AddParameter(it));
                        return $"({QuoteColumn(col)} IN ({string.Join(", ", placeholders)}))";
                    }
                }
            }
        }

        // object.Equals(a, b)
        if (call.Method.Name == "Equals")
        {
            if (call.Object != null && call.Arguments.Count == 1)
            {
                return HandleEquality(call.Object, call.Arguments[0], isNotEqual: false);
            }
            if (call.Arguments.Count == 2)
            {
                return HandleEquality(call.Arguments[0], call.Arguments[1], isNotEqual: false);
            }
        }

        throw new NotSupportedException($"LINQ method call '{call.Method.DeclaringType?.Name}.{call.Method.Name}' is not supported in SQL translation.");
    }

    /// <summary>
    /// Safely extracts values from closures, fields, and properties without dynamic code generation (Native AOT compliant).
    /// </summary>
    private static object? ExtractValue(Expression? expr)
    {
        if (expr == null) return null;
        if (expr is ConstantExpression c) return c.Value;

        if (expr is MemberExpression m)
        {
            var container = ExtractValue(m.Expression);
            if (m.Member is FieldInfo field)
            {
                return field.GetValue(container);
            }
            if (m.Member is PropertyInfo prop)
            {
                return prop.GetValue(container);
            }
        }

        if (expr is UnaryExpression u && (u.NodeType == ExpressionType.Convert || u.NodeType == ExpressionType.ConvertChecked))
        {
            return ExtractValue(u.Operand);
        }

        // Fallback for parameterless method invocations
        if (expr is MethodCallExpression call && call.Object == null && call.Arguments.Count == 0)
        {
            return call.Method.Invoke(null, null);
        }

        // Fallback: only attempt dynamic compilation if dynamic code is supported (non-NativeAOT)
        if (RuntimeFeature.IsDynamicCodeSupported)
        {
            try
            {
                return Expression.Lambda(expr).Compile().DynamicInvoke();
            }
            catch
            {
                return null;
            }
        }

        throw new NotSupportedException(
            $"LINQ expression '{expr}' cannot be evaluated in Native AOT mode without dynamic code generation. " +
            "Please extract the value into a local variable before using it in the query expression.");
    }
}
