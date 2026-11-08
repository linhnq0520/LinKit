using System;

namespace LinKit.Core.Inbox;

/// <summary>
/// Marks a message consumer or CQRS command handler as idempotent, so a redelivered message is processed only once. The
/// generated consumer records the message identity together with the expiration window and skips duplicates that are still
/// inside it.
/// </summary>
[AttributeUsage(
    AttributeTargets.Class | AttributeTargets.Method,
    Inherited = false,
    AllowMultiple = false
)]
public sealed class IdempotentAttribute : Attribute
{
    /// <summary>
    /// Gets or sets how long a processed message stays in the inbox store, in minutes. Defaults to 1440 (24 hours).
    /// Choose a window longer than the broker's maximum redelivery delay.
    /// </summary>
    public int ExpirationMinutes { get; set; } = 1440;

    /// <summary>
    /// Initializes the attribute.
    /// </summary>
    /// <param name="expirationMinutes">How long a processed message stays in the inbox store, in minutes.</param>
    public IdempotentAttribute(int expirationMinutes = 1440)
    {
        ExpirationMinutes = expirationMinutes;
    }
}
