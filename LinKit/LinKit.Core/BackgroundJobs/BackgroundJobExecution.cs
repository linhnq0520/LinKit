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
        string? executionId = null,
        Action? onComplete = null
    )
    {
        executionId ??= Guid.NewGuid().ToString("N");
        using IServiceScope scope = serviceProvider.CreateScope();

        var history = new JobExecutionHistory
        {
            ExecutionId = executionId,
            JobName = config.Name,
            StartTime = DateTime.UtcNow,
            EmbeddedData = embeddedData,
            IsSuccess = false,
        };

        using var logScope = logger?.BeginScope(
            new Dictionary<string, object>
            {
                ["ExecutionId"] = executionId,
                ["JobName"] = config.Name,
            }
        );

        try
        {
            logger?.LogDebug(
                "Executing job instance for [{Job}] with ExecutionId [{ExecutionId}].",
                config.Name,
                executionId
            );
            IBackgroundJobMapper? backgroundJobMapper =
                scope.ServiceProvider.GetKeyedService<IBackgroundJobMapper>(config.AssemblyName);

            Func<IMediator, CancellationToken, Task>? executor = backgroundJobMapper?.GetExecutor(
                config.Name,
                embeddedData,
                executionId
            );
            if (executor == null)
            {
                history.ErrorMessage = "Job executor not found.";
                logger?.LogWarning(
                    "Job executor not found for job: {Job} (ExecutionId: {ExecutionId})",
                    config.Name,
                    executionId
                );
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
            logger?.LogDebug(
                "Finished job instance for [{Job}] with ExecutionId [{ExecutionId}].",
                config.Name,
                executionId
            );
        }
        catch (Exception ex)
        {
            history.IsSuccess = false;
            history.ErrorMessage = ex.ToString();
            logger?.LogError(
                ex,
                "Error executing job instance for {Job} (ExecutionId: {ExecutionId})",
                config.Name,
                executionId
            );
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
                logger?.LogError(
                    exLog,
                    "Error saving job history for {Job} (ExecutionId: {ExecutionId})",
                    config.Name,
                    executionId
                );
            }

            onComplete?.Invoke();
        }
    }
}
