using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;

namespace LinKit.Data.Linq;

/// <summary>
/// High-performance, AOT-friendly LINQ Query Provider backed by LinKit compile-time Fluent Query.
/// </summary>
public sealed class LinKitQueryProvider<TEntity> : ILinKitAsyncQueryProvider where TEntity : class
{
    private readonly Func<IFluentQuery<TEntity>> _fluentQueryFactory;
    private readonly DatabaseDialect _dialect;
    private readonly IReadOnlyDictionary<string, string> _propertyToColumn;

    public LinKitQueryProvider(
        Func<IFluentQuery<TEntity>> fluentQueryFactory,
        DatabaseDialect dialect,
        IReadOnlyDictionary<string, string> propertyToColumn)
    {
        _fluentQueryFactory = fluentQueryFactory;
        _dialect = dialect;
        _propertyToColumn = propertyToColumn;
    }

    public IQueryable CreateQuery(Expression expression)
    {
        return new LinKitQueryable<TEntity>(this, expression);
    }

    public IQueryable<TElement> CreateQuery<TElement>(Expression expression)
    {
        if (typeof(TElement) == typeof(TEntity))
        {
            return (IQueryable<TElement>)(object)new LinKitQueryable<TEntity>(this, expression);
        }

        throw new NotSupportedException($"LinKitQueryProvider only supports querying entity type '{typeof(TEntity).Name}'. Projections to custom types can be mapped after ToListAsync().");
    }

    public object? Execute(Expression expression)
    {
        return Execute<object?>(expression);
    }

    public TResult Execute<TResult>(Expression expression)
    {
        return ExecuteAsync<TResult>(expression, CancellationToken.None).GetAwaiter().GetResult();
    }

    public async Task<TResult> ExecuteAsync<TResult>(Expression expression, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var fq = BuildFluentQuery(expression, out var terminalMethod, out var translated);

        if (terminalMethod is "Count" or "LongCount" || typeof(TResult) == typeof(long) || typeof(TResult) == typeof(int))
        {
            long count = translated.Take == 0
                ? 0
                : ApplyPaging(await fq.CountAsync(ct), translated.Skip, translated.Take);
            return (TResult)Convert.ChangeType(count, typeof(TResult));
        }

        if (terminalMethod == "Any" || typeof(TResult) == typeof(bool))
        {
            bool any = translated.Take != 0 &&
                ApplyPaging(await fq.CountAsync(ct), translated.Skip, translated.Take) > 0;
            return (TResult)(object)any;
        }

        if (terminalMethod is "FirstOrDefault" or "First" or "SingleOrDefault" or "Single" ||
            typeof(TResult) == typeof(TEntity))
        {
            var single = terminalMethod is "Single" or "SingleOrDefault";
            var items = await fq.Take(single ? 2 : 1).ToListAsync(ct);
            return terminalMethod switch
            {
                "First" when items.Count == 0 => throw new InvalidOperationException("Sequence contains no elements."),
                "Single" when items.Count == 0 => throw new InvalidOperationException("Sequence contains no elements."),
                "SingleOrDefault" or "Single" when items.Count > 1
                    => throw new InvalidOperationException("Sequence contains more than one element."),
                _ => (TResult)(object?)items.FirstOrDefault()!
            };
        }

        if (typeof(TResult) == typeof(List<TEntity>) || typeof(TResult) == typeof(IEnumerable<TEntity>))
        {
            var list = await fq.ToListAsync(ct);
            return (TResult)(object)list;
        }

        throw new NotSupportedException($"Execution of return type '{typeof(TResult).Name}' with terminal '{terminalMethod}' is not supported.");
    }

    public async Task<PagedResult<T>> ExecutePagedAsync<T>(Expression expression, int pageNumber, int pageSize, CancellationToken ct = default) where T : class
    {
        if (typeof(T) != typeof(TEntity))
        {
            throw new NotSupportedException($"ExecutePagedAsync only supports entity type '{typeof(TEntity).Name}'.");
        }

        var fq = BuildFluentQuery(expression, out _, out _);
        var paged = await fq.ToPagedListAsync(pageNumber, pageSize, ct);
        return (PagedResult<T>)(object)paged;
    }

    private static long ApplyPaging(long count, int? skip, int? take)
    {
        if (skip is > 0) count = Math.Max(0, count - skip.Value);
        if (take.HasValue) count = Math.Min(count, Math.Max(0, take.Value));
        return count;
    }

    internal IFluentQuery<TEntity> BuildFluentQuery(
        Expression expression,
        out string? terminalMethod,
        out TranslatedSqlQuery translated)
    {
        terminalMethod = null;
        Expression? terminalPredicate = null;
        var translator = new ExpressionSqlTranslator(_dialect, _propertyToColumn);

        // Check for terminal aggregate methods like Count / Any / FirstOrDefault
        if (expression is MethodCallExpression call)
        {
            if (call.Method.Name is "Count" or "LongCount" or "Any" or "FirstOrDefault" or "First" or "SingleOrDefault" or "Single")
            {
                terminalMethod = call.Method.Name;
                if (call.Arguments.Count > 1)
                {
                    terminalPredicate = call.Arguments[1];
                    expression = call.Arguments[0];
                }
                else
                {
                    expression = call.Arguments[0];
                }
            }
        }

        translated = translator.Translate(expression, terminalPredicate);
        var fq = _fluentQueryFactory();

        if (!string.IsNullOrWhiteSpace(translated.WhereSql))
        {
            fq.Where(translated.WhereSql, translated.Parameters);
        }

        for (var index = 0; index < translated.OrderBys.Count; index++)
        {
            var (col, isDesc) = translated.OrderBys[index];
            if (index == 0)
            {
                if (isDesc) fq.OrderByDescending(col);
                else fq.OrderBy(col);
            }
            else if (isDesc)
            {
                fq.ThenByDescending(col);
            }
            else
            {
                fq.ThenBy(col);
            }
        }

        if (translated.Skip.HasValue)
        {
            fq.Skip(translated.Skip.Value);
        }

        if (translated.Take.HasValue)
        {
            fq.Take(translated.Take.Value);
        }

        return fq;
    }
}
