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
    Task<TResult> ExecuteAsync<TResult>(Expression expression, CancellationToken ct = default);
    Task<PagedResult<T>> ExecutePagedAsync<T>(Expression expression, int pageNumber, int pageSize, CancellationToken ct = default) where T : class;
}
