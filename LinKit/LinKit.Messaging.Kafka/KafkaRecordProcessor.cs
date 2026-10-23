using Microsoft.Extensions.Logging;

namespace LinKit.Messaging.Kafka;

internal static class KafkaRecordProcessor
{
    internal static async Task ProcessAsync(
        Func<Task> processMessage,
        Action commitOffset,
        Action seekToRecord,
        string topic,
        string? key,
        TimeSpan retryDelay,
        CancellationToken cancellationToken,
        ILogger? logger,
        int attempt = 1,
        int maxAttempts = 0,
        Func<Exception, Task>? deadLetter = null)
    {
        try
        {
            await processMessage();
            commitOffset();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (maxAttempts > 0 && attempt >= maxAttempts && deadLetter != null)
            {
                logger?.LogCritical(
                    ex,
                    "Kafka message from topic {Topic} (Key: {Key}) failed {Attempts} times; dead-lettering.",
                    topic,
                    key,
                    attempt);
                // If DLQ publishing fails we fall through to the normal retry path, keeping at-least-once.
                try
                {
                    await deadLetter(ex);
                    commitOffset();
                    return;
                }
                catch (Exception dlqEx) when (dlqEx is not OperationCanceledException)
                {
                    logger?.LogError(dlqEx, "Dead-letter publish failed for topic {Topic}; the record will be retried.", topic);
                }
            }

            logger?.LogError(
                ex,
                "Error processing Kafka message from topic {Topic} (Key: {Key}), attempt {Attempt}; the record will be retried.",
                topic,
                key,
                attempt);
            seekToRecord();
            await Task.Delay(retryDelay, cancellationToken);
        }
    }
}
