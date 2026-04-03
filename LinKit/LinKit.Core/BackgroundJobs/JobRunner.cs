using LinKit.Core.Cqrs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LinKit.Core.BackgroundJobs;

public class JobRunner(JobConfig config, IServiceProvider sp)
{
    private readonly IServiceProvider _serviceProvider = sp;
    private readonly ILogger<JobRunner>? _logger = sp.GetService<ILogger<JobRunner>>();
    private readonly CancellationTokenSource _cts = new();
    private Task? _runnerTask;
    private SemaphoreSlim? _parallelLimiter;

    public JobConfig CurrentConfig { get; } = config;

    public void Start()
    {
        _runnerTask = Task.Run(RunAsync);
    }

    public void Stop()
    {
        _cts.Cancel();
        try
        {
            _runnerTask?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException ex) when (ex.InnerException is TaskCanceledException) { }
        catch (OperationCanceledException) { }
        finally
        {
            _cts.Dispose();
            _parallelLimiter?.Dispose();
        }
    }

    private async Task ExecuteJobLogicAsync()
    {
        using IServiceScope scope = _serviceProvider.CreateScope();

        var history = new JobExecutionHistory
        {
            JobName = CurrentConfig.Name,
            StartTime = DateTime.UtcNow,
            EmbeddedData = CurrentConfig.EmbeddedData,
            IsSuccess = false,
        };

        try
        {
            _logger?.LogDebug("Executing job instance for [{Job}].", CurrentConfig.Name);
            IBackgroundJobMapper? backgroundJobMapper =
                scope.ServiceProvider.GetKeyedService<IBackgroundJobMapper>(
                    CurrentConfig.AssemblyName
                );

            var executor = backgroundJobMapper?.GetExecutor(
                CurrentConfig.Name,
                CurrentConfig.EmbeddedData
            );
            if (executor == null)
            {
                history.ErrorMessage = "Job executor not found.";
                _logger?.LogWarning("Job executor not found for job: {Job}", CurrentConfig.Name);
                return;
            }

            IMediator mediator =
                scope.ServiceProvider.GetKeyedService<IMediator>(CurrentConfig.AssemblyName)
                ?? scope.ServiceProvider.GetService<IMediator>()
                ?? throw new InvalidOperationException(
                    $"IMediator is not registered for Assembly: {CurrentConfig.AssemblyName}"
                );

            await executor(mediator, _cts.Token);

            history.IsSuccess = true;
            _logger?.LogDebug("Finished job instance for [{Job}].", CurrentConfig.Name);
        }
        catch (Exception ex)
        {
            history.IsSuccess = false;
            history.ErrorMessage = ex.ToString();
            _logger?.LogError(ex, "Error executing job instance for {Job}", CurrentConfig.Name);
        }
        finally
        {
            history.EndTime = DateTime.UtcNow;
            try
            {
                // Lấy ra Logger (Có thể là DB do User tự viết, hoặc File mặc định)
                var historyLogger = scope.ServiceProvider.GetService<IJobHistoryLogger>();
                if (historyLogger != null && CurrentConfig.IsLogHistory)
                {
                    await historyLogger.LogAsync(history, _cts.Token);
                }
            }
            catch (Exception exLog)
            {
                _logger?.LogError(exLog, "Error saving job history for {Job}", CurrentConfig.Name);
            }

            _parallelLimiter?.Release();
        }
    }

    private async Task RunAsync()
    {
        _logger?.LogInformation(
            "Job [{Job}] started with schedule type: {ScheduleType}.",
            CurrentConfig.Name,
            CurrentConfig.ScheduleType
        );
        _parallelLimiter = new SemaphoreSlim(CurrentConfig.MaxParallel);

        if (CurrentConfig.RunOnStart)
        {
            _logger?.LogInformation(
                "Job [{Job}] configured to RunOnStart. Executing immediately.",
                CurrentConfig.Name
            );
            try
            {
                await _parallelLimiter.WaitAsync(_cts.Token);
                _ = Task.Run(ExecuteJobLogicAsync, _cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger?.LogError(
                    ex,
                    "Error executing RunOnStart for job {Job}",
                    CurrentConfig.Name
                );
            }
        }

        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                TimeSpan delay = Scheduler.GetNextDelay(CurrentConfig);

                if (delay.TotalMilliseconds < 0)
                {
                    delay = TimeSpan.Zero;
                }

                if (delay > TimeSpan.FromDays(40))
                {
                    _logger?.LogWarning(
                        "Calculated delay for job [{Job}] is unusually long ({Delay}). Capping at 5 minutes.",
                        CurrentConfig.Name,
                        delay
                    );
                    delay = TimeSpan.FromMinutes(5);
                }

                _logger?.LogInformation(
                    "Job [{Job}] will run next in {Delay}.",
                    CurrentConfig.Name,
                    delay
                );
                await Task.Delay(delay, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (_cts.Token.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await _parallelLimiter.WaitAsync(_cts.Token);
                _ = Task.Run(ExecuteJobLogicAsync, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(
                    ex,
                    "Error scheduling job execution for {Job}",
                    CurrentConfig.Name
                );
                await Task.Delay(TimeSpan.FromSeconds(30), _cts.Token);
            }
        }

        _logger?.LogInformation("Job [{Job}] stopped.", CurrentConfig.Name);
    }
}
