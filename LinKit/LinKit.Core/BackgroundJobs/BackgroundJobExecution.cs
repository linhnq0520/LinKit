using LinKit.Core.Cqrs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LinKit.Core.BackgroundJobs;

internal static class BackgroundJobExecution
{
    public static async Task ExecuteAsync(
        JobConfig config,
        string embeddedData,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken,
        ILogger? logger = null,
        Action? onComplete = null
    )
    {
        using IServiceScope scope = serviceProvider.CreateScope();

        var history = new JobExecutionHistory
        {
            JobName = config.Name,
            StartTime = DateTime.UtcNow,
            EmbeddedData = embeddedData,
            IsSuccess = false,
        };

        try
        {
            logger?.LogDebug("Executing job instance for [{Job}].", config.Name);
            IBackgroundJobMapper? backgroundJobMapper =
                scope.ServiceProvider.GetKeyedService<IBackgroundJobMapper>(config.AssemblyName);

            Func<IMediator, CancellationToken, Task>? executor = backgroundJobMapper?.GetExecutor(
                config.Name,
                embeddedData
            );
            if (executor == null)
            {
                history.ErrorMessage = "Job executor not found.";
                logger?.LogWarning("Job executor not found for job: {Job}", config.Name);
                return;
            }

            IMediator mediator =
                scope.ServiceProvider.GetKeyedService<IMediator>(config.AssemblyName)
                ?? scope.ServiceProvider.GetService<IMediator>()
                ?? throw new InvalidOperationException(
                    $"IMediator is not registered for Assembly: {config.AssemblyName}"
                );

            await executor(mediator, cancellationToken);

            history.IsSuccess = true;
            logger?.LogDebug("Finished job instance for [{Job}].", config.Name);
        }
        catch (Exception ex)
        {
            history.IsSuccess = false;
            history.ErrorMessage = ex.ToString();
            logger?.LogError(ex, "Error executing job instance for {Job}", config.Name);
        }
        finally
        {
            history.EndTime = DateTime.UtcNow;
            try
            {
                var historyLogger = scope.ServiceProvider.GetService<IJobHistoryLogger>();
                if (historyLogger != null && config.IsLogHistory)
                {
                    await historyLogger.LogAsync(history, cancellationToken);
                }
            }
            catch (Exception exLog)
            {
                logger?.LogError(exLog, "Error saving job history for {Job}", config.Name);
            }

            onComplete?.Invoke();
        }
    }
}
