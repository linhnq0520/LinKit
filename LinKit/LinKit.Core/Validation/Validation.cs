using System;
using System.Collections.Generic;
using System.Linq;
using LinKit.Core.Results;

namespace LinKit.Core.Validation;

/// <summary>
/// Defines a synchronous validation contract for instances of <typeparamref name="T"/>.
/// LinKit does not currently discover or invoke validators automatically; applications
/// must register and call them through their own pipeline.
/// </summary>
public interface IValidator<in T>
{
    IEnumerable<ValidationError> Validate(T instance);
}

/// <summary>
/// Exception thrown when validation fails in request handling pipeline.
/// </summary>
public class ValidationException : Exception
{
    public IReadOnlyList<ValidationError> Errors { get; }

    public ValidationException(IEnumerable<ValidationError> errors)
        : base("One or more validation failures have occurred.")
    {
        Errors = errors.ToList().AsReadOnly();
    }

    public ValidationException(string propertyName, string errorMessage)
        : this(new[] { new ValidationError(propertyName, errorMessage) })
    {
    }
}
