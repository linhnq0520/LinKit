using LinKit.Core.Cqrs;
using Shared;
using Test1.Infarstructure;

namespace Test.Application
{
    [CqrsContext(typeof(InfraCommand), typeof(Program), typeof(TransactionBehavior<,>))]
    public class CqrsContextx
    {
    }
}
