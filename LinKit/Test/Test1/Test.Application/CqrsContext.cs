using LinKit.Core.Cqrs;
using Test1.Infarstructure;

namespace Test.Application
{
    [CqrsContext(typeof(InfraCommand), typeof(Program))]
    public class CqrsContextx { }
}
