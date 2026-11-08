using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace LinKit.Core.BackgroundJobs;

/// <summary>
/// Appends job history to a file as one JSON object per line, which stays readable and can be shipped by a log collector.
/// Writes are serialized by a semaphore because <see cref="File.AppendAllTextAsync(string, string, CancellationToken)"/>
/// is not safe for concurrent writers on the same path.
/// </summary>
public class FileJobHistoryLogger : IJobHistoryLogger
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // Immutable once built; reuse instead of allocating per write.
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    // Serialize through the source-generated JsonTypeInfo so the method stays trimming- and AOT-safe.
    private static readonly JobHistoryJsonContext SerializerContext = new(SerializerOptions);

    /// <summary>
    /// Initializes the logger.
    /// </summary>
    /// <param name="filePath">
    /// Path of the file to append to. The directory must already exist; the file is created on the first write.
    /// </param>
    public FileJobHistoryLogger(string filePath)
    {
        _filePath = filePath;
    }

    /// <summary>
    /// Appends one JSON line describing the execution.
    /// </summary>
    /// <param name="history">The record to append.</param>
    /// <param name="cancellationToken">Token that bounds the write.</param>
    /// <returns>A task that completes when the line has been flushed.</returns>
    public async Task LogAsync(
        JobExecutionHistory history,
        CancellationToken cancellationToken = default
    )
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var json = JsonSerializer.Serialize(history, SerializerContext.JobExecutionHistory);
            await File.AppendAllTextAsync(_filePath, json + Environment.NewLine, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }
}
