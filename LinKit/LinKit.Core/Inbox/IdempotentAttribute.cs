using System;

namespace LinKit.Core.Inbox;

/// <summary>
/// Marks a message consumer or CQRS command handler as idempotent.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class IdempotentAttribute : Attribute
{
    public int ExpirationMinutes { get; set; } = 1440;

    public IdempotentAttribute(int expirationMinutes = 1440)
    {
        ExpirationMinutes = expirationMinutes;
    }
}
