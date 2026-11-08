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
public sealed class LinKitQueryProvider<TEntity> : ILinKitAsyncQueryProvider
    where TEntity : class
{
    private readonly Func<IFluentQuery<TEntity>> _fluentQueryFactory;
    private readonly DatabaseDialect _dialect;
    private readonly IReadOnlyDictionary<string, string> _propertyToColumn;

    /// <summary>Creates the provider.</summary>
    /// <param name="fluentQueryFactory">Creates the fluent query that receives the translated clauses.</param>
    /// <param name="dialect">Dialect used to quote identifiers.</param>
    /// <param name="propertyToColumn">Maps CLR property names to physical column names.</param>
    public LinKitQueryProvider(
        Func<IFluentQuery<TEntity>> fluentQueryFactory,
        DatabaseDialect dialect,
        IReadOnlyDictionary<string, string> propertyToColumn
    )
    {
        _fluentQueryFactory = fluentQueryFactory;
        _dialect = dialect;
        _propertyToColumn = propertyToColumn;
    }

    /// <inheritdoc/>
    public IQueryable CreateQuery(Expression expression)
    {
        return new LinKitQueryable<TEntity>(this, expression);
    }

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">The element type differs from <typeparamref name="TEntity"/>.</exception>
    /// <remarks>Projection to a custom type is rejected; materialise the entity first and project in memory.</remarks>
    public IQueryable<TElement> CreateQuery<TElement>(Expression expression)
    {
        if (typeof(TElement) == typeof(TEntity))
        {
            return (IQueryable<TElement>)(object)new LinKitQueryable<TEntity>(this, expression);
        }

        throw new NotSupportedException(
            $"LinKitQueryProvider only supports querying entity type '{typeof(TEntity).Name}'. Projections to custom types can be mapped after ToListAsync()."
        );
    }

    /// <inheritdoc/>
    public object? Execute(Expression expression)
    {
        return Execute<object?>(expression);
    }

    /// <inheritdoc/>
    /// <remarks>Blocks on the asynchronous path; prefer <see cref="ExecuteAsync{TResult}"/> from asynchronous code.</remarks>
    public TResult Execute<TResult>(Expression expression)
    {
        // IQueryable<T> is synchronously enumerable, so a synchronous terminal has to block. ASP.NET Core and
        // console hosts install no SynchronizationContext, so this cannot deadlock there; async callers should use
        // ILinKitQueryable instead, which reaches ExecuteAsync directly.
        return ExecuteAsync<TResult>(expression, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <inheritdoc/>
    public async Task<TResult> ExecuteAsync<TResult>(
        Expression expression,
        CancellationToken ct = default
    )
    {
        ct.ThrowIfCancellationRequested();
        var fq = BuildFluentQuery(expression, out var terminalMethod, out var translated);

        // The terminal method drives the execution shape. ILinKitQueryable has no terminal in the expression tree,
        // so it falls back to the requested result type, which is unambiguous because projections are rejected by
        // CreateQuery<TElement> long before they reach this point.
        if (terminalMethod is "Count" or "LongCount")
        {
            return ToCount<TResult>(await CountAsync(fq, translated, ct).ConfigureAwait(false));
        }

        if (terminalMethod is "Any")
        {
            return (TResult)(object)await AnyAsync(fq, translated, ct).ConfigureAwait(false);
        }

        if (terminalMethod is "First" or "FirstOrDefault")
        {
            var entity = await fq.FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (entity is null && terminalMethod == "First")
                throw new InvalidOperationException("Sequence contains no elements.");
            return entity is null ? default! : (TResult)(object)entity;
        }

        if (terminalMethod is "Single" or "SingleOrDefault")
        {
            var rows = await fq.Take(2).ToListAsync(ct).ConfigureAwait(false);
            return RowsToSingle<TResult>(rows, terminalMethod == "Single");
        }

        if (terminalMethod is not null)
        {
            throw new NotSupportedException($"LINQ terminal '{terminalMethod}' is not supported.");
        }

        if (typeof(TResult) == typeof(bool))
        {
            return (TResult)(object)await AnyAsync(fq, translated, ct).ConfigureAwait(false);
        }

        if (
            typeof(TResult) == typeof(long)
            || typeof(TResult) == typeof(int)
            || typeof(TResult) == typeof(short)
        )
        {
            return ToCount<TResult>(await CountAsync(fq, translated, ct).ConfigureAwait(false));
        }

        if (typeof(TResult) == typeof(TEntity))
        {
            var entity = await fq.FirstOrDefaultAsync(ct).ConfigureAwait(false);
            return entity is null ? default! : (TResult)(object)entity;
        }

        if (
            typeof(TResult) == typeof(List<TEntity>)
            || typeof(TResult) == typeof(IEnumerable<TEntity>)
            || typeof(TResult) == typeof(object)
        )
        {
            var list = await fq.ToListAsync(ct).ConfigureAwait(false);
            return (TResult)(object)list;
        }

        throw new NotSupportedException(
            $"Execution of return type '{typeof(TResult).Name}' with terminal '{terminalMethod ?? "<none>"}' is not supported."
        );
    }

    private static async Task<long> CountAsync(
        IFluentQuery<TEntity> fq,
        TranslatedSqlQuery translated,
        CancellationToken ct
    )
    {
        if (translated.Take == 0)
            return 0;
        // IFluentQuery.CountAsync counts the filtered set and ignores the paging window, so the window is applied here.
        return ApplyPaging(
            await fq.CountAsync(ct).ConfigureAwait(false),
            translated.Skip,
            translated.Take
        );
    }

    private static async Task<bool> AnyAsync(
        IFluentQuery<TEntity> fq,
        TranslatedSqlQuery translated,
        CancellationToken ct
    )
    {
        if (translated.Take == 0)
            return false;
        // AnyAsync issues SELECT 1 ... LIMIT 1 but ignores Skip; fall back to a 1-row fetch when offset is present.
        return translated.Skip is > 0
            ? (await fq.Take(1).ToListAsync(ct).ConfigureAwait(false)).Count > 0
            : await fq.AnyAsync(ct).ConfigureAwait(false);
    }

    private static TResult ToCount<TResult>(long count) =>
        typeof(TResult) == typeof(long) ? (TResult)(object)count
        : typeof(TResult) == typeof(object) ? (TResult)(object)count
        : typeof(TResult) == typeof(int) ? (TResult)(object)checked((int)count)
        : typeof(TResult) == typeof(short) ? (TResult)(object)checked((short)count)
        : throw new NotSupportedException(
            $"A count cannot be returned as '{typeof(TResult).Name}'."
        );

    private static TResult RowsToSingle<TResult>(List<TEntity> rows, bool required) =>
        rows.Count switch
        {
            0 when required => throw new InvalidOperationException(
                "Sequence contains no elements."
            ),
            0 => default!,
            1 => (TResult)(object)rows[0],
            _ => throw new InvalidOperationException("Sequence contains more than one element."),
        };

    /// <inheritdoc/>
    public async Task<PagedResult<T>> ExecutePagedAsync<T>(
        Expression expression,
        int pageNumber,
        int pageSize,
        CancellationToken ct = default
    )
        where T : class
    {
        if (typeof(T) != typeof(TEntity))
        {
            throw new NotSupportedException(
                $"ExecutePagedAsync only supports entity type '{typeof(TEntity).Name}'."
            );
        }

        var fq = BuildFluentQuery(expression, out _, out _);
        var paged = await fq.ToPagedListAsync(pageNumber, pageSize, ct);
        return (PagedResult<T>)(object)paged;
    }

    private static long ApplyPaging(long count, int? skip, int? take)
    {
        if (skip is > 0)
            count = Math.Max(0, count - skip.Value);
        if (take.HasValue)
            count = Math.Min(count, Math.Max(0, take.Value));
        return count;
    }

    internal IFluentQuery<TEntity> BuildFluentQuery(
        Expression expression,
        out string? terminalMethod,
        out TranslatedSqlQuery translated
    )
    {
        terminalMethod = null;
        Expression? terminalPredicate = null;
        var translator = new ExpressionSqlTranslator(_dialect, _propertyToColumn);

        // Check for terminal aggregate methods like Count / Any / FirstOrDefault
        if (expression is MethodCallExpression call)
        {
            if (
                call.Method.Name
                is "Count"
                    or "LongCount"
                    or "Any"
                    or "FirstOrDefault"
                    or "First"
                    or "SingleOrDefault"
                    or "Single"
            )
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
                if (isDesc)
                    fq.OrderByDescending(col);
                else
                    fq.OrderBy(col);
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
