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
        _parallelLimiter = new SemaphoreSlim(CurrentConfig.MaxParallel);
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
            _parallelLimiter = null;
        }
    }

    public async Task TriggerNowAsync(
        string? embeddedDataOverride,
        CancellationToken cancellationToken = default
    )
    {
        if (_parallelLimiter == null)
        {
            throw new InvalidOperationException(
                $"Job [{CurrentConfig.Name}] is not running and cannot be triggered."
            );
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _cts.Token
        );

        await _parallelLimiter.WaitAsync(linked.Token);
        try
        {
            string embeddedData = embeddedDataOverride ?? CurrentConfig.EmbeddedData;
            string executionId = Guid.NewGuid().ToString("N");
            await BackgroundJobExecution.ExecuteAsync(
                CurrentConfig,
                embeddedData,
                _serviceProvider,
                linked.Token,
                _logger,
                executionId
            );
        }
        finally
        {
            _parallelLimiter.Release();
        }
    }

    private Task ExecuteJobLogicAsync()
    {
        string executionId = Guid.NewGuid().ToString("N");
        _logger?.LogDebug(
            "Scheduling job execution for [{Job}] with ExecutionId [{ExecutionId}].",
            CurrentConfig.Name,
            executionId
        );

        return BackgroundJobExecution.ExecuteAsync(
            CurrentConfig,
            CurrentConfig.EmbeddedData,
            _serviceProvider,
            _cts.Token,
            _logger,
            executionId,
            onComplete: () => _parallelLimiter?.Release()
        );
    }

    private async Task RunAsync()
    {
        _logger?.LogInformation(
            "Job [{Job}] started with schedule type: {ScheduleType}.",
            CurrentConfig.Name,
            CurrentConfig.ScheduleType
        );

        if (CurrentConfig.RunOnStart)
        {
            _logger?.LogInformation(
                "Job [{Job}] configured to RunOnStart. Executing immediately.",
                CurrentConfig.Name
            );
            try
            {
                await _parallelLimiter!.WaitAsync(_cts.Token);
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
                await _parallelLimiter!.WaitAsync(_cts.Token);
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
