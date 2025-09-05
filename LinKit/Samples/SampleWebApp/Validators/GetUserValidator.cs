using LinKit.Core.Abstractions;
using SampleWebApp.Contracts.Behaviors;
using SampleWebApp.Features.Users;

namespace SampleWebApp.Validators;

[RegisterService(Lifetime.Transient)]
public class GetUserValidator : IValidator<GetUserQuery>
{
    public void Validate(GetUserQuery value)
    {
        var id = value.Id;
        if (id < 0)
        {
            throw new Exception("Id must be greater than 0.");
        }
        Console.WriteLine("Validated GetUserQuery");
    }
}
