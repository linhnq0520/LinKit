using LinKit.Core.Abstractions;

namespace SampleWebApp.Validators
{
    public interface IABC { }
    [RegisterService(lifetime:Lifetime.Scoped)]
    public class CreateUserValidator : IABC
    {
    }
}
