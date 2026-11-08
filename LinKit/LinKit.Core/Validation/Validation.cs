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
/// <typeparam name="T">Type being validated.</typeparam>
public interface IValidator<in T>
{
    /// <summary>
    /// Validates <paramref name="instance"/>.
    /// </summary>
    /// <param name="instance">The instance to validate.</param>
    /// <returns>
    /// One <see cref="ValidationError"/> per failed rule; an empty sequence means the instance is valid.
    /// </returns>
    IEnumerable<ValidationError> Validate(T instance);
}

/// <summary>
/// Exception thrown when validation fails in request handling pipeline.
/// </summary>
public class ValidationException : Exception
{
    /// <summary>
    /// Gets the individual validation failures that caused this exception.
    /// </summary>
    public IReadOnlyList<ValidationError> Errors { get; }

    /// <summary>
    /// Initializes the exception from a set of validation failures.
    /// </summary>
    /// <param name="errors">The failures to report. The sequence is materialised, so it must not be lazy over a
    /// disposed resource.</param>
    public ValidationException(IEnumerable<ValidationError> errors)
        : base("One or more validation failures have occurred.")
    {
        Errors = errors.ToList().AsReadOnly();
    }

    /// <summary>
    /// Initializes the exception for a single failed property.
    /// </summary>
    /// <param name="propertyName">Name of the property that failed validation.</param>
    /// <param name="errorMessage">Human readable description of the failure.</param>
    public ValidationException(string propertyName, string errorMessage)
        : this(new[] { new ValidationError(propertyName, errorMessage) }) { }
}
