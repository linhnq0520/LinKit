using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;

namespace LinKit.Data;

internal abstract record SqlNode;

internal sealed record ColumnNode(string Alias, ColumnModel Column) : SqlNode;

internal sealed record ValueNode(object? Value, ColumnModel? Column = null) : SqlNode;

internal sealed record BinaryNode(SqlNode Left, string Operator, SqlNode Right) : SqlNode;

internal sealed record NullNode(SqlNode Operand, bool Negate) : SqlNode;

internal sealed record NotNode(SqlNode Operand) : SqlNode;

internal sealed record LikeNode(SqlNode Operand, string Pattern) : SqlNode;

internal sealed record InNode(SqlNode Operand, IReadOnlyList<object?> Values) : SqlNode;

internal sealed record AggregateNode(string Function, SqlNode? Operand, bool Distinct = false)
    : SqlNode;

/// <summary>
/// A scalar SQL function call. Names that differ per provider are resolved when the statement is rendered, so one
/// expression tree stays portable across dialects.
/// </summary>
internal sealed record FunctionNode(
    string Name,
    IReadOnlyList<SqlNode> Arguments,
    bool Distinct = false,
    string? SqlServerName = null,
    string? MySqlName = null,
    string? OracleName = null
) : SqlNode;

internal sealed record ExistsNode(SelectPlan Plan, bool Negate) : SqlNode;

/// <summary>
/// Root of the strongly typed SQL expression tree used to build portable predicates, projections and orderings.
/// </summary>
/// <remarks>
/// Expressions are declarative only: nothing is evaluated in process. The tree is translated to SQL when the query runs,
/// so a predicate can be composed and passed around without touching a database.
/// </remarks>
public abstract class SqlExpression
{
    internal abstract SqlNode Node { get; }
}

/// <summary>Exposes the expression behind a value that can be used in a SQL predicate.</summary>
/// <typeparam name="T">Type of the value.</typeparam>
public interface ISqlValue<out T>
{
    /// <summary>The expression this value contributes to the surrounding predicate.</summary>
    SqlExpression Expression { get; }
}

/// <summary>Typed expression over a single column or scalar, produced by the FluentQuery column members.</summary>
/// <typeparam name="T">Column type; use <c>T?</c> for a nullable column.</typeparam>
public class SqlExpression<T> : SqlExpression, IProjection<T>, ISqlValue<T>
{
    internal override SqlNode Node { get; }

    /// <summary>This expression, so a column can also be passed as an <see cref="ISqlValue{T}"/>.</summary>
    public SqlExpression Expression => this;
    private readonly Func<DbDataReader, int, T?> _read;

    internal SqlExpression(SqlNode node, Func<DbDataReader, int, T?>? read = null)
    {
        Node = node;
        _read =
            read
            ?? (
                static (reader, ordinal) =>
                    reader.IsDBNull(ordinal) && default(T) is null
                        ? default!
                        : DbValues.Read<T>(reader, ordinal)
            );
    }

    internal T ReadScalar(DbDataReader reader, int ordinal) => _read(reader, ordinal)!;

    /// <summary>Packages this expression as a one column projection so it can be selected on its own.</summary>
    /// <returns>A projection that reads the single scalar this expression renders.</returns>
    public Projection<T> Build() => new([this], reader => _read(reader, 0)!);

    /// <summary>Wraps a constant in an expression, which is what allows <c>column &gt; 5</c> to reach the translator.</summary>
    /// <param name="value">Constant to wrap.</param>
    /// <returns>An expression that renders the value as a command parameter.</returns>
    public static implicit operator SqlExpression<T>(T value) => Sql.Value(value);

    private static Predicate Compare(
        SqlExpression<T>? left,
        string operation,
        SqlExpression<T>? right
    )
    {
        var leftNode = left?.Node ?? new ValueNode(null);
        var rightNode = right?.Node ?? new ValueNode(null);
        if (leftNode is ColumnNode leftColumn && rightNode is ValueNode rightValue)
            rightNode = rightValue with { Column = leftColumn.Column };
        if (rightNode is ColumnNode rightColumn && leftNode is ValueNode leftValue)
            leftNode = leftValue with { Column = rightColumn.Column };
        return new(new BinaryNode(leftNode, operation, rightNode));
    }

    /// <summary>Builds an equality predicate, handling <see langword="null"/> as <c>IS NULL</c>.</summary>
    /// <param name="left">Left operand; <see langword="null"/> is rendered as a null value.</param>
    /// <param name="right">Right operand; <see langword="null"/> is rendered as a null value.</param>
    /// <returns>The composed predicate.</returns>
    public static Predicate operator ==(SqlExpression<T>? left, SqlExpression<T>? right) =>
        Compare(left, "=", right);

    /// <summary>Builds an inequality predicate that treats two <see langword="null"/> values as equal.</summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    /// <returns>The composed predicate.</returns>
    public static Predicate operator !=(SqlExpression<T>? left, SqlExpression<T>? right) =>
        Compare(left, "<>", right);

    /// <summary>Builds a <c>&gt;</c> predicate; comparisons against <see langword="null"/> never match.</summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    /// <returns>The composed predicate.</returns>
    public static Predicate operator >(SqlExpression<T>? left, SqlExpression<T>? right) =>
        Compare(left, ">", right);

    /// <summary>Builds a <c>&lt;</c> predicate; comparisons against <see langword="null"/> never match.</summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    /// <returns>The composed predicate.</returns>
    public static Predicate operator <(SqlExpression<T>? left, SqlExpression<T>? right) =>
        Compare(left, "<", right);

    /// <summary>Builds a <c>&gt;=</c> predicate; comparisons against <see langword="null"/> never match.</summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    /// <returns>The composed predicate.</returns>
    public static Predicate operator >=(SqlExpression<T>? left, SqlExpression<T>? right) =>
        Compare(left, ">=", right);

    /// <summary>Builds a <c>&lt;=</c> predicate; comparisons against <see langword="null"/> never match.</summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    /// <returns>The composed predicate.</returns>
    public static Predicate operator <=(SqlExpression<T>? left, SqlExpression<T>? right) =>
        Compare(left, "<=", right);

    /// <summary>
    /// Identity comparison. The overloaded comparison operators build SQL, so this method must not compare by value.
    /// </summary>
    /// <param name="other">Object to compare.</param>
    /// <returns><see langword="true"/> only when <paramref name="other"/> is the same instance.</returns>
    public override bool Equals(object? other) => ReferenceEquals(this, other);

    /// <summary>Returns the identity hash code, matching <see cref="Equals(object?)"/>.</summary>
    /// <returns>The runtime identity hash of this expression.</returns>
    public override int GetHashCode() =>
        System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);

    private SqlNode Value(T value) => new ValueNode(value, (Node as ColumnNode)?.Column);

    /// <summary>Builds an equality predicate against a constant.</summary>
    /// <param name="value">Constant to compare with; the column metadata is carried to the parameter.</param>
    /// <returns>The composed predicate.</returns>
    public Predicate Equal(T value) => new(new BinaryNode(Node, "=", Value(value)));

    /// <summary>Builds an equality predicate against another column or expression.</summary>
    /// <param name="other">Right operand.</param>
    /// <returns>The composed predicate.</returns>
    public Predicate Equal(SqlExpression<T> other) => new(new BinaryNode(Node, "=", other.Node));

    /// <summary>Builds an inequality predicate against another column or expression.</summary>
    /// <param name="other">Right operand.</param>
    /// <returns>The composed predicate.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="other"/> is <see langword="null"/>.</exception>
    public Predicate NotEqual(SqlExpression<T> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new(new BinaryNode(Node, "<>", other.Node));
    }

    /// <summary>Builds an inequality predicate against a constant.</summary>
    /// <param name="value">Constant to compare with.</param>
    /// <returns>The composed predicate.</returns>
    public Predicate NotEqual(T value) => new(new BinaryNode(Node, "<>", Value(value)));

    /// <summary>Compares two columns (or expressions) with <c>&gt;</c>, for example <c>a.EndsAt.GreaterThan(a.StartsAt)</c>.</summary>
    public Predicate GreaterThan(SqlExpression<T> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new(new BinaryNode(Node, ">", other.Node));
    }

    /// <summary>Builds a <c>&gt;=</c> predicate against another column or expression.</summary>
    /// <param name="other">Right operand.</param>
    /// <returns>The composed predicate.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="other"/> is <see langword="null"/>.</exception>
    public Predicate GreaterThanOrEqual(SqlExpression<T> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new(new BinaryNode(Node, ">=", other.Node));
    }

    /// <summary>Builds a <c>&lt;</c> predicate against another column or expression.</summary>
    /// <param name="other">Right operand.</param>
    /// <returns>The composed predicate.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="other"/> is <see langword="null"/>.</exception>
    public Predicate LessThan(SqlExpression<T> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new(new BinaryNode(Node, "<", other.Node));
    }

    /// <summary>Builds a <c>&lt;=</c> predicate against another column or expression.</summary>
    /// <param name="other">Right operand.</param>
    /// <returns>The composed predicate.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="other"/> is <see langword="null"/>.</exception>
    public Predicate LessThanOrEqual(SqlExpression<T> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new(new BinaryNode(Node, "<=", other.Node));
    }

    /// <summary>Builds a <c>&gt;</c> predicate against a constant.</summary>
    /// <param name="value">Constant to compare with.</param>
    /// <returns>The composed predicate.</returns>
    public Predicate GreaterThan(T value) => new(new BinaryNode(Node, ">", Value(value)));

    /// <summary>Builds a <c>&gt;=</c> predicate against a constant.</summary>
    /// <param name="value">Constant to compare with.</param>
    /// <returns>The composed predicate.</returns>
    public Predicate GreaterThanOrEqual(T value) => new(new BinaryNode(Node, ">=", Value(value)));

    /// <summary>Builds a <c>&lt;</c> predicate against a constant.</summary>
    /// <param name="value">Constant to compare with.</param>
    /// <returns>The composed predicate.</returns>
    public Predicate LessThan(T value) => new(new BinaryNode(Node, "<", Value(value)));

    /// <summary>Builds a <c>&lt;=</c> predicate against a constant.</summary>
    /// <param name="value">Constant to compare with.</param>
    /// <returns>The composed predicate.</returns>
    public Predicate LessThanOrEqual(T value) => new(new BinaryNode(Node, "<=", Value(value)));

    /// <summary>Builds an <c>IS NULL</c> predicate.</summary>
    /// <returns>The composed predicate.</returns>
    public Predicate IsNull() => new(new NullNode(Node, false));

    /// <summary>Builds an <c>IS NOT NULL</c> predicate.</summary>
    /// <returns>The composed predicate.</returns>
    public Predicate IsNotNull() => new(new NullNode(Node, true));

    /// <summary>
    /// Builds an <c>IN</c> predicate. Long lists are split into several <c>IN</c> groups to respect the provider
    /// parameter limit, and <see langword="null"/> entries are folded into an extra <c>IS NULL</c> check.
    /// </summary>
    /// <param name="values">Values to match; an empty sequence never matches.</param>
    /// <returns>The composed predicate.</returns>
    public Predicate In(IEnumerable<T> values) =>
        new(new InNode(Node, values.Cast<object?>().ToArray()));

    /// <summary>Builds an <c>IN</c> predicate from an inline value list.</summary>
    /// <param name="values">Values to match.</param>
    /// <returns>The composed predicate.</returns>
    public Predicate In(params T[] values) => In((IEnumerable<T>)values);

    /// <summary>Negates <see cref="In(IEnumerable{T})"/>, keeping <c>NULL</c> semantics correct.</summary>
    /// <param name="values">Values to exclude; an empty sequence always matches.</param>
    /// <returns>The composed predicate.</returns>
    public Predicate NotIn(IEnumerable<T> values) => !In(values);

    /// <summary>Negates <see cref="In(T[])"/>, keeping <c>NULL</c> semantics correct.</summary>
    /// <param name="values">Values to exclude.</param>
    /// <returns>The composed predicate.</returns>
    public Predicate NotIn(params T[] values) => !In((IEnumerable<T>)values);

    /// <summary>Builds an inclusive <c>BETWEEN</c> predicate.</summary>
    /// <param name="lower">Inclusive lower bound.</param>
    /// <param name="upper">Inclusive upper bound.</param>
    /// <returns>The composed predicate.</returns>
    public Predicate Between(T lower, T upper) =>
        GreaterThanOrEqual(lower) & LessThanOrEqual(upper);

    /// <summary>Orders this expression ascending.</summary>
    /// <returns>An ordering that renders as <c>ASC</c>.</returns>
    public Ordering Asc() => new(this, false);

    /// <summary>Orders this expression descending.</summary>
    /// <returns>An ordering that renders as <c>DESC</c>.</returns>
    public Ordering Desc() => new(this, true);
}

/// <summary>A column expression bound to one table alias, as exposed by the FluentQuery table members.</summary>
/// <typeparam name="T">Column type; use <c>T?</c> for a nullable column.</typeparam>
public sealed class Column<T> : SqlExpression<T>
{
    /// <summary>Creates a column expression.</summary>
    /// <param name="alias">Alias of the table that owns the column.</param>
    /// <param name="column">Column metadata used for quoting and parameter typing.</param>
    /// <param name="read">Optional custom reader; the default converter is used when omitted.</param>
    public Column(string alias, ColumnModel column, Func<DbDataReader, int, T?>? read = null)
        : base(new ColumnNode(alias, column), read) { }

    /// <summary>Creates an assignment for a bulk update.</summary>
    /// <param name="value">Replacement value.</param>
    /// <returns>The assignment consumed by the update builder.</returns>
    public Assignment Set(T value) => new((ColumnNode)Node, value);
}

/// <summary>Immutable boolean SQL predicate, composed with <see cref="And"/>, <see cref="Or"/> and <see cref="Not"/>.</summary>
public sealed class Predicate
{
    internal SqlNode Node { get; }

    internal Predicate(SqlNode node) => Node = node;

    /// <summary>Combines this predicate with another one using <c>AND</c>.</summary>
    /// <param name="other">Predicate to combine.</param>
    /// <returns>The combined predicate.</returns>
    public Predicate And(Predicate other) => new(new BinaryNode(Node, "AND", other.Node));

    /// <summary>Combines this predicate with another one using <c>OR</c>.</summary>
    /// <param name="other">Predicate to combine.</param>
    /// <returns>The combined predicate.</returns>
    public Predicate Or(Predicate other) => new(new BinaryNode(Node, "OR", other.Node));

    /// <summary>Negates this predicate, pushing the negation through <c>IN</c> and <c>IS NULL</c> when possible.</summary>
    /// <returns>The negated predicate.</returns>
    public Predicate Not() => new(new NotNode(Node));

    /// <summary>Shorthand for <see cref="And"/>.</summary>
    /// <param name="left">Left predicate.</param>
    /// <param name="right">Right predicate.</param>
    /// <returns>The combined predicate.</returns>
    public static Predicate operator &(Predicate left, Predicate right) => left.And(right);

    /// <summary>Shorthand for <see cref="Or"/>.</summary>
    /// <param name="left">Left predicate.</param>
    /// <param name="right">Right predicate.</param>
    /// <returns>The combined predicate.</returns>
    public static Predicate operator |(Predicate left, Predicate right) => left.Or(right);

    /// <summary>Shorthand for <see cref="Not"/>.</summary>
    /// <param name="predicate">Predicate to negate.</param>
    /// <returns>The negated predicate.</returns>
    public static Predicate operator !(Predicate predicate) => predicate.Not();
}

/// <summary>One ordering term of an <c>ORDER BY</c> clause.</summary>
/// <param name="Expression">Expression to sort by.</param>
/// <param name="Descending">Whether the term sorts descending.</param>
public sealed record Ordering(SqlExpression Expression, bool Descending);

/// <summary>One <c>SET column = value</c> term of a set-based update.</summary>
public sealed class Assignment
{
    internal ColumnNode Column { get; }
    internal object? Value { get; }

    internal Assignment(ColumnNode column, object? value)
    {
        Column = column;
        Value = value;
    }
}

/// <summary>
/// Entry point for the SQL expression DSL: aggregates, scalar functions and string matching that render into the query.
/// </summary>
/// <remarks>
/// Functions with provider specific names (for example <c>LENGTH</c> versus <c>LEN</c>) are resolved when the statement
/// is rendered, so the same expression works on every supported database.
/// </remarks>
public static class Sql
{
    /// <summary>Compares a nullable column with a non-nullable one, keeping the nullable side explicit.</summary>
    /// <typeparam name="T">Underlying value type.</typeparam>
    /// <param name="left">Nullable expression.</param>
    /// <param name="right">Non-nullable expression.</param>
    /// <returns>The composed predicate.</returns>
    public static Predicate Equal<T>(this SqlExpression<T?> left, SqlExpression<T> right)
        where T : struct => left.Equal(Nullable(right));

    /// <summary>Compares a non-nullable column with a nullable one, keeping the nullable side explicit.</summary>
    /// <typeparam name="T">Underlying value type.</typeparam>
    /// <param name="left">Non-nullable expression.</param>
    /// <param name="right">Nullable expression.</param>
    /// <returns>The composed predicate.</returns>
    public static Predicate Equal<T>(this SqlExpression<T> left, SqlExpression<T?> right)
        where T : struct => Nullable(left).Equal(right);

    /// <summary><c>COUNT(*)</c>, which counts rows and never returns <see langword="null"/>.</summary>
    /// <returns>An expression holding the row count.</returns>
    public static SqlExpression<long> Count() => new(new AggregateNode("COUNT", null));

    /// <summary><c>SUM(expression)</c>; returns <see langword="null"/> when no row matches.</summary>
    /// <param name="expression">Numeric expression to sum.</param>
    /// <returns>An expression holding the sum.</returns>
    public static SqlExpression<decimal?> Sum(SqlExpression<decimal> expression) =>
        new(
            new AggregateNode("SUM", expression.Node),
            static (reader, ordinal) =>
                reader.IsDBNull(ordinal) ? null : DbValues.Read<decimal>(reader, ordinal)
        );

    /// <summary><c>AVG(expression)</c>; returns <see langword="null"/> when no row matches.</summary>
    /// <param name="expression">Numeric expression to average.</param>
    /// <returns>An expression holding the average.</returns>
    public static SqlExpression<decimal?> Average(SqlExpression<decimal> expression) =>
        new(
            new AggregateNode("AVG", expression.Node),
            static (reader, ordinal) =>
                reader.IsDBNull(ordinal) ? null : DbValues.Read<decimal>(reader, ordinal)
        );

    /// <summary><c>MIN(expression)</c>; returns <see langword="null"/> when no row matches.</summary>
    /// <typeparam name="T">Value type to aggregate.</typeparam>
    /// <param name="expression">Expression to aggregate.</param>
    /// <returns>An expression holding the minimum.</returns>
    public static SqlExpression<T?> Min<T>(SqlExpression<T> expression)
        where T : struct =>
        new(
            new AggregateNode("MIN", expression.Node),
            (reader, ordinal) =>
                reader.IsDBNull(ordinal) ? null : expression.ReadScalar(reader, ordinal)
        );

    /// <summary><c>MAX(expression)</c>; returns <see langword="null"/> when no row matches.</summary>
    /// <typeparam name="T">Value type to aggregate.</typeparam>
    /// <param name="expression">Expression to aggregate.</param>
    /// <returns>An expression holding the maximum.</returns>
    public static SqlExpression<T?> Max<T>(SqlExpression<T> expression)
        where T : struct =>
        new(
            new AggregateNode("MAX", expression.Node),
            (reader, ordinal) =>
                reader.IsDBNull(ordinal) ? null : expression.ReadScalar(reader, ordinal)
        );

    /// <summary><c>MIN(text)</c>; returns <see langword="null"/> when no row matches.</summary>
    /// <param name="expression">String expression to aggregate.</param>
    /// <returns>An expression holding the minimum value.</returns>
    public static SqlExpression<string?> Min(ISqlValue<string> expression) =>
        new(new AggregateNode("MIN", expression.Expression.Node));

    /// <summary><c>MAX(text)</c>; returns <see langword="null"/> when no row matches.</summary>
    /// <param name="expression">String expression to aggregate.</param>
    /// <returns>An expression holding the maximum value.</returns>
    public static SqlExpression<string?> Max(ISqlValue<string> expression) =>
        new(new AggregateNode("MAX", expression.Expression.Node));

    /// <summary><c>COUNT(column)</c>, which ignores <c>NULL</c> values unlike <see cref="Count()"/>.</summary>
    public static SqlExpression<long> Count<T>(SqlExpression<T> expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        return new(new AggregateNode("COUNT", expression.Node));
    }

    /// <summary><c>COUNT(DISTINCT column)</c>, answered by the provider without materialising rows.</summary>
    public static SqlExpression<long> CountDistinct<T>(SqlExpression<T> expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        return new(new AggregateNode("COUNT", expression.Node, true));
    }

    /// <summary>
    /// <c>LOWER(value)</c>. Pair it with an escaped <c>Contains</c>/<c>StartsWith</c> pattern for case-insensitive
    /// search without a full scan on a case-sensitive collation.
    /// </summary>
    public static SqlExpression<string> Lower(ISqlValue<string> expression) =>
        new(new FunctionNode("LOWER", [expression.Expression.Node]));

    /// <summary><c>UPPER(value)</c>.</summary>
    public static SqlExpression<string> Upper(ISqlValue<string> expression) =>
        new(new FunctionNode("UPPER", [expression.Expression.Node]));

    /// <summary>
    /// Character length: <c>LENGTH</c> on PostgreSQL/SQLite/Oracle, <c>LEN</c> on SQL Server and <c>CHAR_LENGTH</c>
    /// on MySQL (whose <c>LENGTH</c> counts bytes, which would be wrong for multi-byte text).
    /// </summary>
    public static SqlExpression<long?> Length(ISqlValue<string> expression) =>
        new(
            new FunctionNode(
                "LENGTH",
                [expression.Expression.Node],
                SqlServerName: "LEN",
                MySqlName: "CHAR_LENGTH"
            ),
            static (reader, ordinal) =>
                reader.IsDBNull(ordinal) ? null : DbValues.Read<long>(reader, ordinal)
        );

    /// <summary><c>TRIM(value)</c>, supported verbatim by every supported provider.</summary>
    public static SqlExpression<string> Trim(ISqlValue<string> expression) =>
        new(new FunctionNode("TRIM", [expression.Expression.Node]));

    /// <summary>
    /// <c>COALESCE(expression, fallback)</c>. The fallback is bound with the same column metadata as the expression,
    /// so decimal fallbacks keep their scale on SQLite.
    /// </summary>
    public static SqlExpression<T> Coalesce<T>(SqlExpression<T> expression, T fallback)
    {
        ArgumentNullException.ThrowIfNull(expression);
        return new(
            new FunctionNode(
                "COALESCE",
                [expression.Node, new ValueNode(fallback, (expression.Node as ColumnNode)?.Column)]
            )
        );
    }

    /// <summary>Reinterprets a non-nullable expression as nullable so it can be compared with a nullable column.</summary>
    /// <typeparam name="T">Underlying value type.</typeparam>
    /// <param name="expression">Expression to reinterpret.</param>
    /// <returns>An expression that reads <see langword="null"/> for <c>NULL</c>.</returns>
    public static SqlExpression<T?> Nullable<T>(SqlExpression<T> expression)
        where T : struct =>
        new(
            expression.Node,
            (reader, ordinal) =>
                reader.IsDBNull(ordinal) ? null : expression.ReadScalar(reader, ordinal)
        );

    /// <summary>Reinterprets a reference expression so <c>NULL</c> is read as <see langword="null"/>.</summary>
    /// <typeparam name="T">Reference type.</typeparam>
    /// <param name="expression">Expression to reinterpret.</param>
    /// <returns>An expression that reads <see langword="null"/> for <c>NULL</c>.</returns>
    public static SqlExpression<T?> Optional<T>(SqlExpression<T> expression)
        where T : class =>
        new(
            expression.Node,
            (reader, ordinal) =>
                reader.IsDBNull(ordinal) ? null : expression.ReadScalar(reader, ordinal)
        );

    /// <summary>Wraps a constant so it can be compared with a column.</summary>
    /// <typeparam name="T">Constant type.</typeparam>
    /// <param name="value">Constant value.</param>
    /// <returns>An expression that renders the value as a command parameter.</returns>
    public static SqlExpression<T> Value<T>(T value) =>
        new(new ValueNode(value), (reader, ordinal) => value);

    /// <summary>Builds an explicit <c>LIKE</c> predicate with an already formatted pattern.</summary>
    /// <param name="expression">Text expression to match.</param>
    /// <param name="pattern">Pattern including its own <c>%</c> and <c>_</c> wildcards.</param>
    /// <returns>The composed predicate.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pattern"/> is <see langword="null"/>.</exception>
    public static Predicate Like(this ISqlValue<string> expression, string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        return new(new LikeNode(expression.Expression.Node, pattern));
    }

    /// <summary>Builds a case-sensitive <c>LIKE '%text%'</c> predicate with the input escaped.</summary>
    /// <param name="expression">Text expression to search.</param>
    /// <param name="text">Literal text; wildcards inside it are escaped and matched literally.</param>
    /// <returns>The composed predicate.</returns>
    public static Predicate Contains(this ISqlValue<string> expression, string text) =>
        Like(expression, text, "%", "%");

    /// <summary>Builds a <c>LIKE 'text%'</c> predicate with the input escaped.</summary>
    /// <param name="expression">Text expression to search.</param>
    /// <param name="text">Literal prefix; wildcards inside it are escaped and matched literally.</param>
    /// <returns>The composed predicate.</returns>
    public static Predicate StartsWith(this ISqlValue<string> expression, string text) =>
        Like(expression, text, string.Empty, "%");

    /// <summary>Builds a <c>LIKE '%text'</c> predicate with the input escaped.</summary>
    /// <param name="expression">Text expression to search.</param>
    /// <param name="text">Literal suffix; wildcards inside it are escaped and matched literally.</param>
    /// <returns>The composed predicate.</returns>
    public static Predicate EndsWith(this ISqlValue<string> expression, string text) =>
        Like(expression, text, "%", string.Empty);

    private static Predicate Like(
        ISqlValue<string> expression,
        string text,
        string prefix,
        string suffix
    )
    {
        ArgumentNullException.ThrowIfNull(text);
        var escaped = text.Replace("~", "~~")
            .Replace("%", "~%")
            .Replace("_", "~_")
            .Replace("[", "~[");
        return new(new LikeNode(expression.Expression.Node, prefix + escaped + suffix));
    }
}

internal sealed class SqlWriter(SqlDialect dialect, string? tenantId)
{
    public SqlDialect Dialect { get; } = dialect;
    public List<SqlArgument> Arguments { get; } = [];

    public string Parameter(object? value, ColumnModel? column = null)
    {
        if (Arguments.Count >= Dialect.MaxParameters)
            throw new NotSupportedException("Query exceeds the provider parameter limit.");
        var name = Dialect.Parameter(Arguments.Count);
        Arguments.Add(new(value, column));
        return
            Dialect.Kind == DatabaseKind.Sqlite
            && (value is decimal || column?.Type == System.Data.DbType.Decimal)
            ? $"CAST({name} AS NUMERIC)"
            : name;
    }

    public string Render(SqlNode node, IReadOnlySet<string> aliases) =>
        node switch
        {
            ColumnNode column => aliases.Contains(column.Alias)
                ? Dialect.Quote(column.Alias) + "." + Dialect.Quote(column.Column.Name)
                : throw new InvalidOperationException(
                    $"Alias '{column.Alias}' is not in this query."
                ),
            ValueNode value => Parameter(value.Value, value.Column),
            BinaryNode binary when binary.Operator is "=" or "<>" => Equality(binary, aliases),
            BinaryNode binary when binary.Operator is ">" or "<" or ">=" or "<=" => Comparison(
                binary,
                aliases
            ),
            BinaryNode binary =>
                $"({Render(binary.Left, aliases)} {binary.Operator} {Render(binary.Right, aliases)})",
            NullNode check =>
                $"({Render(check.Operand, aliases)} IS {(check.Negate ? "NOT " : "")}NULL)",
            NotNode { Operand: InNode list } => NotIn(list, aliases),
            NotNode not => $"NOT ({Render(not.Operand, aliases)})",
            LikeNode like => RenderLike(like, aliases),
            InNode list => In(list, aliases),
            AggregateNode aggregate => RenderAggregate(aggregate, aliases),
            FunctionNode function => RenderFunction(function, aliases),
            ExistsNode exists =>
                $"{(exists.Negate ? "NOT " : "")}EXISTS ({Select(exists.Plan, aliases)})",
            _ => throw new NotSupportedException("Unsupported SQL node."),
        };

    private string RenderAggregate(AggregateNode aggregate, IReadOnlySet<string> aliases)
    {
        var function =
            aggregate.Function == "COUNT" && Dialect.Kind == DatabaseKind.SqlServer
                ? "COUNT_BIG"
                : aggregate.Function;
        var operand = aggregate.Operand is null ? "*" : Render(aggregate.Operand, aliases);
        return $"{function}({(aggregate.Distinct ? "DISTINCT " : "")}{operand})";
    }

    private string RenderFunction(FunctionNode function, IReadOnlySet<string> aliases)
    {
        var name = Dialect.Kind switch
        {
            DatabaseKind.SqlServer => function.SqlServerName ?? function.Name,
            DatabaseKind.MySql => function.MySqlName ?? function.Name,
            DatabaseKind.Oracle => function.OracleName ?? function.Name,
            _ => function.Name,
        };
        var arguments = string.Join(
            ", ",
            function.Arguments.Select(argument => Render(argument, aliases))
        );
        return $"{name}({(function.Distinct ? "DISTINCT " : "")}{arguments})";
    }

    private string Equality(BinaryNode binary, IReadOnlySet<string> aliases)
    {
        if (binary.Left is ValueNode { Value: null } && binary.Right is ValueNode { Value: null })
            return binary.Operator == "=" ? "(1 = 1)" : "(1 = 0)";
        if (binary.Left is ValueNode { Value: null })
            return $"({Render(binary.Right, aliases)} IS {(binary.Operator == "<>" ? "NOT " : "")}NULL)";
        if (binary.Right is ValueNode { Value: null })
            return $"({Render(binary.Left, aliases)} IS {(binary.Operator == "<>" ? "NOT " : "")}NULL)";
        var left = Render(binary.Left, aliases);
        var right = Render(binary.Right, aliases);
        var leftMayBeNull = MayBeNull(binary.Left);
        var rightMayBeNull = MayBeNull(binary.Right);

        if (binary.Operator == "=" && (!leftMayBeNull || !rightMayBeNull))
            return $"({left} = {right})";
        if (binary.Operator == "<>" && !leftMayBeNull && !rightMayBeNull)
            return $"({left} <> {right})";
        if (binary.Operator == "<>" && !leftMayBeNull)
            return $"({right} IS NULL OR {left} <> {right})";
        if (binary.Operator == "<>" && !rightMayBeNull)
            return $"({left} IS NULL OR {left} <> {right})";

        var equal =
            $"(({left} IS NOT NULL AND {right} IS NOT NULL AND {left} = {right}) OR ({left} IS NULL AND {right} IS NULL))";
        return binary.Operator == "=" ? equal : $"NOT {equal}";
    }

    private string Comparison(BinaryNode binary, IReadOnlySet<string> aliases)
    {
        if (IsNullValue(binary.Left) || IsNullValue(binary.Right))
            return "(1 = 0)";
        var left = Render(binary.Left, aliases);
        var right = Render(binary.Right, aliases);
        var comparison = $"{left} {binary.Operator} {right}";
        return MayBeNull(binary.Left) || MayBeNull(binary.Right)
            ? $"({left} IS NOT NULL AND {right} IS NOT NULL AND {comparison})"
            : $"({comparison})";
    }

    private string RenderLike(LikeNode like, IReadOnlySet<string> aliases)
    {
        var operand = Render(like.Operand, aliases);
        var pattern = Parameter(like.Pattern, new("pattern", System.Data.DbType.String));
        var comparison = $"{operand} LIKE {pattern} ESCAPE '~'";
        return MayBeNull(like.Operand) ? $"({operand} IS NOT NULL AND {comparison})" : comparison;
    }

    private static bool MayBeNull(SqlNode node) =>
        node switch
        {
            ValueNode { Value: not null and not DBNull } => false,
            ColumnNode { Column.Nullable: false } => false,
            AggregateNode { Function: "COUNT" } => false,
            FunctionNode { Name: "COALESCE" } => false,
            _ => true,
        };

    private static bool IsNullValue(SqlNode node) => node is ValueNode { Value: null or DBNull };

    private string In(InNode list, IReadOnlySet<string> aliases)
    {
        if (list.Values.Count == 0)
            return "(1 = 0)";
        var operand = Render(list.Operand, aliases);
        var nonNull = list.Values.Where(value => value is not null).ToArray();
        var containsNull = nonNull.Length != list.Values.Count;
        var clause =
            nonNull.Length == 0 ? null : JoinInChunks(operand, nonNull, list.Operand, "OR");
        return containsNull
            ? clause is null
                ? $"{operand} IS NULL"
                : $"({clause} OR {operand} IS NULL)"
            : clause!;
    }

    private string NotIn(InNode list, IReadOnlySet<string> aliases)
    {
        if (list.Values.Count == 0)
            return "(1 = 1)";
        var operand = Render(list.Operand, aliases);
        var nonNull = list.Values.Where(value => value is not null).ToArray();
        var containsNull = nonNull.Length != list.Values.Count;
        if (nonNull.Length == 0)
            return $"{operand} IS NOT NULL";
        var clause = JoinInChunks(operand, nonNull, list.Operand, "AND", negate: true);
        return containsNull
            ? $"({operand} IS NOT NULL AND {clause})"
            : $"({operand} IS NULL OR {clause})";
    }

    private string JoinInChunks(
        string operand,
        object?[] values,
        SqlNode node,
        string separator,
        bool negate = false
    )
    {
        var column = (node as ColumnNode)?.Column;
        var chunks = values
            .Chunk(Dialect.MaxInItems)
            .Select(chunk =>
            {
                var parameters = string.Join(", ", chunk.Select(value => Parameter(value, column)));
                return $"{operand} {(negate ? "NOT " : "")}IN ({parameters})";
            });
        var joined = string.Join($" {separator} ", chunks);
        return values.Length > Dialect.MaxInItems ? $"({joined})" : joined;
    }

    private string Table(TableSource source) =>
        Dialect.Table(source.Model) + " " + Dialect.Quote(source.Alias);

    /// <summary>
    /// Tenant and soft-delete predicates for one table. They are emitted as ordinary <c>WHERE</c> conditions (or as
    /// part of a join's <c>ON</c> clause) instead of filtering a derived table, so the provider keeps seeing the base
    /// table and can still use its indexes.
    /// </summary>
    private List<string> Filters(TableSource source)
    {
        var clauses = new List<string>();
        foreach (var column in source.Model.Columns)
        {
            if (column.Tenant)
            {
                if (string.IsNullOrWhiteSpace(tenantId))
                    throw new InvalidOperationException(
                        "A tenant-scoped table requires DataSession.TenantId."
                    );
                clauses.Add(
                    Dialect.Quote(source.Alias)
                        + "."
                        + Dialect.Quote(column.Name)
                        + " = "
                        + Parameter(tenantId, column)
                );
            }
            if (column.SoftDelete)
                clauses.Add(ActiveRowPredicate(column, source.Alias));
        }
        return clauses;
    }

    private HashSet<string> Aliases(SelectPlan plan, IReadOnlySet<string>? outer)
    {
        var aliases = outer is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(outer, StringComparer.Ordinal);
        foreach (var source in new[] { plan.From }.Concat(plan.Joins.Select(join => join.Source)))
            if (!aliases.Add(source.Alias))
                throw new InvalidOperationException(
                    $"Duplicate or shadowed alias '{source.Alias}'."
                );
        return aliases;
    }

    /// <summary>
    /// Renders everything after the projection keyword: <c>FROM</c>, joins, <c>WHERE</c>, <c>GROUP BY</c> and
    /// <c>HAVING</c>. Parameters are appended in the same order they appear in the emitted text, which keeps the
    /// argument list aligned with its placeholders for <c>COUNT</c>, existence probes and scalar aggregates.
    /// </summary>
    private string FromWhere(
        SelectPlan plan,
        IReadOnlySet<string> aliases,
        IReadOnlySet<string>? outer = null
    )
    {
        var sql = "FROM " + Table(plan.From);
        var joinAliases = outer is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(outer, StringComparer.Ordinal);
        joinAliases.Add(plan.From.Alias);
        foreach (var join in plan.Joins)
        {
            joinAliases.Add(join.Source.Alias);
            sql +=
                (join.Kind == JoinKind.Left ? " LEFT JOIN " : " INNER JOIN ")
                + Table(join.Source)
                + " ON "
                + Render(join.On, joinAliases);
            // A joined table's policy filters belong in its ON clause: in WHERE they would silently turn an outer
            // join into an inner join.
            foreach (var filter in Filters(join.Source))
                sql += " AND " + filter;
        }
        var conditions = new List<string>(plan.Joins.Count + 2);
        if (plan.Where is not null)
            conditions.Add(Render(plan.Where, aliases));
        conditions.AddRange(Filters(plan.From));
        if (conditions.Count > 0)
            sql += " WHERE " + string.Join(" AND ", conditions);
        if (plan.Group.Count > 0)
            sql +=
                " GROUP BY " + string.Join(", ", plan.Group.Select(node => Render(node, aliases)));
        if (plan.Having is not null)
            sql += " HAVING " + Render(plan.Having, aliases);
        return sql;
    }

    private string? OrderBy(SelectPlan plan, IReadOnlySet<string> aliases) =>
        plan.Order.Count == 0
            ? null
            : string.Join(
                ", ",
                plan.Order.Select(item =>
                    Render(item.Expression.Node, aliases) + (item.Descending ? " DESC" : " ASC")
                )
            );

    public string Select(SelectPlan plan, IReadOnlySet<string>? outer = null)
    {
        var aliases = Aliases(plan, outer);
        var select = string.Join(
            ", ",
            plan.Projection.Select(
                (expression, index) =>
                {
                    var outputName = expression is ColumnNode column
                        ? column.Column.Name
                        : "c" + index;
                    return Render(expression, aliases) + " AS " + Dialect.Quote(outputName);
                }
            )
        );
        var sql =
            "SELECT "
            + (plan.Distinct ? "DISTINCT " : "")
            + select
            + " "
            + FromWhere(plan, aliases, outer);
        return Dialect.Paginate(
            sql,
            OrderBy(plan, aliases),
            plan.Skip,
            plan.Take,
            value => Parameter(value)
        );
    }

    /// <summary>
    /// Renders <c>COUNT(*)</c> without projecting any column. A plain <c>SELECT COUNT(*) FROM ... WHERE ...</c> is
    /// emitted whenever the plan has no grouping or distinct modifier, because a projection-free count lets the
    /// provider use a covering index instead of materialising rows only to throw them away again.
    /// </summary>
    public string Count(SelectPlan plan)
    {
        var count = Dialect.Kind == DatabaseKind.SqlServer ? "COUNT_BIG" : "COUNT";
        var aliases = Aliases(plan, null);
        var from = FromWhere(plan, aliases);
        if (
            plan.Group.Count == 0
            && plan.Having is null
            && !plan.Distinct
            && plan.Skip is null
            && plan.Take is null
        )
            return $"SELECT {count}(*) {from}";
        // A paged or grouped count has to count the rows the window would return, so the window is rendered as a
        // projection-free subquery (SELECT 1 ...) and counted from the outside.
        var inner = Dialect.Paginate(
            $"SELECT 1 {from}",
            OrderBy(plan, aliases),
            plan.Skip,
            plan.Take,
            value => Parameter(value)
        );
        return $"SELECT {count}(*) FROM ({inner}) {Dialect.Quote("count_result")}";
    }

    /// <summary>
    /// Renders an existence probe: a single <c>SELECT 1</c> row instead of counting the whole result set.
    /// Ordering is dropped unless the plan pages with <c>Skip</c>, because existence does not depend on row order
    /// and an unnecessary <c>ORDER BY</c> forces the provider to sort before it can answer.
    /// </summary>
    public string Probe(SelectPlan plan)
    {
        if (plan.Take == 0)
            return "SELECT 1 WHERE 1 = 0";
        var aliases = Aliases(plan, null);
        var from = FromWhere(plan, aliases);
        if (plan.Skip is null)
        {
            return Dialect.Kind switch
            {
                DatabaseKind.SqlServer => "SELECT TOP 1 1 " + from,
                DatabaseKind.Oracle => "SELECT 1 " + from + " FETCH FIRST 1 ROWS ONLY",
                _ => "SELECT 1 " + from + " LIMIT 1",
            };
        }
        return Dialect.Paginate(
            "SELECT 1 " + from,
            OrderBy(plan, aliases),
            plan.Skip,
            1,
            value => Parameter(value)
        );
    }

    /// <summary>
    /// Renders a single aggregate such as <c>SELECT SUM("Price") FROM ... WHERE ...</c> without the projection the
    /// query carries. Paging and grouping are rejected rather than silently ignored, since an aggregate over a page
    /// is not the aggregate of the filtered set.
    /// </summary>
    public string Scalar(SelectPlan plan, SqlNode expression)
    {
        if (
            plan.Group.Count != 0
            || plan.Having is not null
            || plan.Distinct
            || plan.Skip is not null
            || plan.Take is not null
            || plan.Order.Count != 0
        )
            throw new NotSupportedException(
                "Scalar aggregates require a query without grouping, distinct, ordering or paging."
            );
        var aliases = Aliases(plan, null);
        return "SELECT " + Render(expression, aliases) + " " + FromWhere(plan, aliases);
    }

    public string Mutate(
        SelectPlan plan,
        IReadOnlyList<Assignment> assignments,
        bool delete,
        long? expectedVersion
    )
    {
        if (plan.Where is null)
            throw new InvalidOperationException(
                "Set-based writes require an explicit Where predicate."
            );
        if (
            plan.Joins.Count != 0
            || plan.Group.Count != 0
            || plan.Having is not null
            || plan.Skip is not null
            || plan.Take is not null
            || plan.Distinct
            || plan.Order.Count != 0
        )
            throw new NotSupportedException(
                "Set-based writes support one table with a predicate, without joins, grouping, ordering or paging."
            );
        var source = plan.From;
        var aliases = new HashSet<string>(StringComparer.Ordinal) { source.Alias };
        var sets = new List<string>();
        var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var assignment in assignments)
        {
            var column = assignment.Column.Column;
            if (assignment.Column.Alias != source.Alias || !source.Model.Columns.Contains(column))
                throw new ArgumentException("Assignment is not from the target table.");
            if (
                column.PrimaryKey
                || column.Generated
                || column.Tenant
                || column.SoftDelete
                || column.Version
            )
                throw new ArgumentException(
                    "Keys and policy columns cannot be assigned by a bulk update."
                );
            if (!assigned.Add(column.Name))
                throw new ArgumentException("Duplicate assignment.");
            if (assignment.Value is null && !column.Nullable)
                throw new ArgumentException("Cannot assign null to a non-nullable column.");
            sets.Add(Dialect.Quote(column.Name) + " = " + Parameter(assignment.Value, column));
        }
        if (!delete && sets.Count == 0)
            throw new ArgumentException("At least one assignment is required.");
        var conditions = new List<string> { Render(plan.Where, aliases) };
        var soft = source.Model.Columns.SingleOrDefault(column => column.SoftDelete);
        foreach (var column in source.Model.Columns)
        {
            var qualified = Dialect.Quote(source.Alias) + "." + Dialect.Quote(column.Name);
            if (column.Tenant)
            {
                if (string.IsNullOrWhiteSpace(tenantId))
                    throw new InvalidOperationException("A tenant is required for this write.");
                conditions.Add(qualified + " = " + Parameter(tenantId, column));
            }
            if (column.SoftDelete)
                conditions.Add(ActiveRowPredicate(column, source.Alias));
            if (column.Version)
            {
                if (expectedVersion is null or < 1 or long.MaxValue)
                    throw new ArgumentException(
                        "A positive original version is required for versioned bulk writes."
                    );
                conditions.Add(qualified + " = " + Parameter(expectedVersion, column));
                if (!delete || soft is not null)
                    sets.Add(Dialect.Quote(column.Name) + " = " + qualified + " + 1");
            }
        }
        if (delete && soft is not null)
        {
            var deletedValue =
                soft.Type == System.Data.DbType.Boolean ? Dialect.True : "CURRENT_TIMESTAMP";
            sets.Add(Dialect.Quote(soft.Name) + " = " + deletedValue);
        }
        var where = string.Join(" AND ", conditions);
        var target = Dialect.Table(source.Model);
        var alias = Dialect.Quote(source.Alias);
        var from = target + (Dialect.Kind == DatabaseKind.Oracle ? " " : " AS ") + alias;
        if (delete && soft is null)
            return Dialect.Kind is DatabaseKind.MySql or DatabaseKind.SqlServer
                ? $"DELETE {alias} FROM {from} WHERE {where}"
                : $"DELETE FROM {from} WHERE {where}";
        return Dialect.Kind == DatabaseKind.SqlServer
            ? $"UPDATE {alias} SET {string.Join(", ", sets)} FROM {from} WHERE {where}"
            : $"UPDATE {from} SET {string.Join(", ", sets)} WHERE {where}";
    }

    private string ActiveRowPredicate(ColumnModel column, string? alias = null)
    {
        var identifier = alias is null
            ? Dialect.Quote(column.Name)
            : Dialect.Quote(alias) + "." + Dialect.Quote(column.Name);
        if (column.Type == System.Data.DbType.Boolean)
            return identifier + " = " + Dialect.False;
        if (!column.Nullable)
            throw new InvalidOperationException(
                $"Timestamp soft-delete column '{column.Name}' must be nullable."
            );
        return identifier + " IS NULL";
    }
}

/// <summary>One bound command parameter together with the column metadata that gives it a database type.</summary>
/// <param name="Value">Parameter value; <see langword="null"/> renders as <c>NULL</c>.</param>
/// <param name="Column">Source column used to infer <see cref="System.Data.DbType"/> and scale, when known.</param>
public sealed record SqlArgument(object? Value, ColumnModel? Column);

/// <summary>A rendered SQL statement and the parameters it binds, in placeholder order.</summary>
/// <param name="Sql">SQL text with dialect specific placeholders.</param>
/// <param name="Arguments">Values to bind, in the order they appear in <paramref name="Sql"/>.</param>
public sealed record CommandSpec(string Sql, IReadOnlyList<SqlArgument> Arguments);
