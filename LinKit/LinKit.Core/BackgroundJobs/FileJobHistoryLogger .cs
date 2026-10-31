using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace LinKit.Core.BackgroundJobs;

public class FileJobHistoryLogger : IJobHistoryLogger
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // Immutable once built; reuse instead of allocating per write.
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        TypeInfoResolver = JobHistoryJsonContext.Default,
    };

    public FileJobHistoryLogger(string filePath)
    {
        _filePath = filePath;
    }

    public async Task LogAsync(
        JobExecutionHistory history,
        CancellationToken cancellationToken = default
    )
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var json = JsonSerializer.Serialize(history, SerializerOptions);
            await File.AppendAllTextAsync(_filePath, json + Environment.NewLine, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }
}
