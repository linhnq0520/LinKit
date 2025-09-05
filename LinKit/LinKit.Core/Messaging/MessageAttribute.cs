namespace LinKit.Core.Messaging;

[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class MessageAttribute : Attribute
{
    public string TopicOrExchange { get; }
    public string? RoutingKey { get; set; }
    public string? QueueName { get; set; }

    public MessageAttribute(string topicOrExchange)
    {
        TopicOrExchange = topicOrExchange;
    }
}