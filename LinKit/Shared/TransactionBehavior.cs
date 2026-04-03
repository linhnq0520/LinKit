using LinKit.Core.Cqrs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Shared
{
    [CqrsBehavior(typeof(ICommand), 0)]
    public sealed class TransactionBehavior<TRequest, TResponse>()
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : ICommand<TResponse>
    {
        public Task<TResponse> HandleAsync(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }
    }
}
