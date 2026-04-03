using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace LinKit.Core.BackgroundJobs;

public class FileJobHistoryLogger : IJobHistoryLogger
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);

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
            var options = new JsonSerializerOptions
            {
                Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
                TypeInfoResolver = JobHistoryJsonContext.Default,
            };

            var json = JsonSerializer.Serialize(history, options);
            await File.AppendAllTextAsync(_filePath, json + Environment.NewLine, cancellationToken);
        }
        catch
        {
            // Bỏ qua lỗi ghi file
        }
        finally
        {
            _lock.Release();
        }
    }
}
