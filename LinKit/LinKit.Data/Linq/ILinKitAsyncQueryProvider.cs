using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;

namespace LinKit.Data.Linq;

/// <summary>
/// Interface for query providers capable of asynchronous evaluation.
/// </summary>
public interface ILinKitAsyncQueryProvider : IQueryProvider
{
    /// <summary>Executes the query asynchronously on the database.</summary>
    /// <typeparam name="TResult">Expected result type.</typeparam>
    /// <param name="expression">Expression tree of the query.</param>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns>The query result.</returns>
    Task<TResult> ExecuteAsync<TResult>(Expression expression, CancellationToken ct = default);

    /// <summary>Executes the query asynchronously and returns one page plus the total count.</summary>
    /// <typeparam name="T">Row type.</typeparam>
    /// <param name="expression">Expression tree of the query.</param>
    /// <param name="pageNumber">One-based page number.</param>
    /// <param name="pageSize">Rows per page.</param>
    /// <param name="ct">Token used to cancel the commands.</param>
    /// <returns>The page and the total row count.</returns>
    Task<PagedResult<T>> ExecutePagedAsync<T>(
        Expression expression,
        int pageNumber,
        int pageSize,
        CancellationToken ct = default
    )
        where T : class;
}
