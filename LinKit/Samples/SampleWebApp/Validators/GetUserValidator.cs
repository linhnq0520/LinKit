using LinKit.Core.Abstractions;
using SampleWebApp.Contracts.Behaviors;
using SampleWebApp.Features.Users;

namespace SampleWebApp.Validators;

[RegisterService(Lifetime.Transient)]
public class GetUserValidator : IValidator<GetUserQuery>
{
    public void Validate(GetUserQuery value)
    {
        Console.WriteLine("Validated GetUserQuery");
    }
}
