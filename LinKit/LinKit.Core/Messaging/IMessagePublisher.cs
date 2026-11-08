namespace LinKit.Core.Messaging;

/// <summary>
/// Publishes messages to the configured broker. Prefer this over <see cref="IBrokerProducer"/> in application code: it
/// resolves the destination from <see cref="MessageAttribute"/> and stamps the standard headers, while
/// <see cref="IBrokerProducer"/> requires the destination on every call.
/// </summary>
public interface IMessagePublisher
{
    /// <summary>
    /// Publishes <paramref name="message"/> to the destination declared by its type.
    /// </summary>
    /// <typeparam name="TMessage">Message type being published.</typeparam>
    /// <param name="message">The message to publish.</param>
    /// <param name="cancellationToken">Token that cancels the publish.</param>
    /// <returns>A task that completes when the broker acknowledged the message.</returns>
    Task PublishAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default);
}
