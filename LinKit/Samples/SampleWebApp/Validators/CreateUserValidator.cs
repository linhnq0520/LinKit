using LinKit.Core.Abstractions;

namespace SampleWebApp.Validators
{
    public interface IABC<T> where T: class { }


    [RegisterService(Lifetime.Scoped,isGeneric: true)]
    public class CreateUserValidator<T> : IABC<T> where T: class
    {
    }
}
