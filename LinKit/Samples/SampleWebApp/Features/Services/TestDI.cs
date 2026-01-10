using LinKit.Core.Abstractions;

namespace SampleWebApp.Features.Services
{
    public interface ITest1: ITest3 { }
    public interface ITest2 { }
    public interface ITest3 { }

    [RegisterService(Lifetime.Scoped)]
    [RegisterService(Lifetime.Scoped, serviceType: typeof(ITest3))]
    public class TestDI : ITest2, ITest1
    {
    }
}
