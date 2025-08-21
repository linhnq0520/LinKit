namespace SampleWebApp.Contracts.Behaviors;

public interface IValidator { }
public interface IValidator<T> : IValidator
{
    void Validate(T value);
}
