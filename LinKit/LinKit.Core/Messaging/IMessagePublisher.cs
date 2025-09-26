namespace LinKit.Core.Messaging;

public interface IMessagePublisher
{
    Task PublishAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default);
}
