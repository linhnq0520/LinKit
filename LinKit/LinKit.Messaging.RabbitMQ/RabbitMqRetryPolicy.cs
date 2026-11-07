using System.Text;

namespace LinKit.Messaging.RabbitMQ;

internal enum RabbitMqFailureAction
{
    Retry,
    DeadLetter,
    Drop
}

internal static class RabbitMqRetryPolicy
{
    internal const string AttemptHeader = "x-linkit-attempt";

    internal static int GetAttemptCount(IReadOnlyDictionary<string, object>? headers)
    {
        if (headers is null || !headers.TryGetValue(AttemptHeader, out var value) || value is null)
            return 0;

        return value switch
        {
            int i => Math.Max(0, i),
            long l => (int)Math.Clamp(l, 0, int.MaxValue),
            short s => Math.Max(0, (int)s),
            byte b => b,
            string s when int.TryParse(s, out var parsed) => Math.Max(0, parsed),
            byte[] bytes when int.TryParse(Encoding.UTF8.GetString(bytes), out var parsed) => Math.Max(0, parsed),
            _ => 0
        };
    }

    internal static RabbitMqFailureAction DecideFailureAction(
        int attemptCount,
        int maxRetryAttempts,
        bool requeueOnFailure,
        bool hasDeadLetterExchange)
    {
        if (requeueOnFailure && attemptCount < maxRetryAttempts)
            return RabbitMqFailureAction.Retry;

        return hasDeadLetterExchange ? RabbitMqFailureAction.DeadLetter : RabbitMqFailureAction.Drop;
    }
}