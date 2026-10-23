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
        var fq = BuildFluentQuery(expression, out var terminalMethod);

        if (terminalMethod == "Count" || typeof(TResult) == typeof(long) || typeof(TResult) == typeof(int))
        {
            long count = await fq.CountAsync(ct);
            return (TResult)Convert.ChangeType(count, typeof(TResult));
        }

        if (terminalMethod == "Any" || typeof(TResult) == typeof(bool))
        {
            bool any = await fq.AnyAsync(ct);
            return (TResult)(object)any;
        }

        if (terminalMethod is "FirstOrDefault" or "First" or "SingleOrDefault" or "Single")
        {
            var item = await fq.FirstOrDefaultAsync(ct);
            return (TResult)(object)item!;
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

        var fq = BuildFluentQuery(expression, out _);
        var paged = await fq.ToPagedListAsync(pageNumber, pageSize, ct);
        return (PagedResult<T>)(object)paged;
    }

    internal IFluentQuery<TEntity> BuildFluentQuery(Expression expression, out string? terminalMethod)
    {
        terminalMethod = null;
        Expression? terminalPredicate = null;
        var translator = new ExpressionSqlTranslator(_dialect, _propertyToColumn);

        // Check for terminal aggregate methods like Count / Any / FirstOrDefault
        if (expression is MethodCallExpression call)
        {
            if (call.Method.Name is "Count" or "Any" or "FirstOrDefault" or "First" or "SingleOrDefault" or "Single")
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

        var translated = translator.Translate(expression, terminalPredicate);
        var fq = _fluentQueryFactory();

        if (!string.IsNullOrWhiteSpace(translated.WhereSql))
        {
            fq.Where(translated.WhereSql, translated.Parameters);
        }

        foreach (var (col, isDesc) in translated.OrderBys)
        {
            if (isDesc) fq.OrderByDescending(col);
            else fq.OrderBy(col);
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
