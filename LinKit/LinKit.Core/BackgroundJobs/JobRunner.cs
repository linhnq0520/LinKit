using System.Text.Json;
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
        try
        {
            _logger?.LogDebug("Executing job instance for [{Job}].", CurrentConfig.Name);
            IBackgroundJobMapper? backgroundJobMapper =
                scope.ServiceProvider.GetKeyedService<IBackgroundJobMapper>(
                    CurrentConfig.AssemblyName
                );
            JobInfo? jobInfo = backgroundJobMapper?.GetJobInfoByName(CurrentConfig.Name);
            if (jobInfo == null)
            {
                _logger?.LogWarning("Job info not found for job: {Job}", CurrentConfig.Name);
                return;
            }
            BackgroundJobCommand instance = (BackgroundJobCommand)jobInfo.Instance!;
            if (!string.IsNullOrWhiteSpace(CurrentConfig.EmbeddedData))
            {
                instance.EmbededData = CurrentConfig.EmbeddedData;
            }
            IMediator mediator = scope.ServiceProvider.GetRequiredKeyedService<IMediator>(
                CurrentConfig.AssemblyName
            );
            if (jobInfo.Executor is null)
            {
                _logger?.LogWarning("Job {Job} Executor is null", CurrentConfig.Name);
            }
            else
            {
                await jobInfo.Executor(mediator, jobInfo.Instance!, _cts.Token);
            }
            _logger?.LogDebug("Finished job instance for [{Job}].", CurrentConfig.Name);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error executing job instance for {Job}", CurrentConfig.Name);
        }
        finally
        {
            _parallelLimiter?.Release(); // Đảm bảo release semaphore
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

        // --- Logic RunOnStart mới ---
        if (CurrentConfig.RunOnStart)
        {
            _logger?.LogInformation(
                "Job [{Job}] configured to RunOnStart. Executing immediately.",
                CurrentConfig.Name
            );
            try
            {
                await _parallelLimiter.WaitAsync(_cts.Token); // Lấy một slot
                _ = Task.Run(ExecuteJobLogicAsync, _cts.Token); // Chạy job
                // Không đợi tác vụ hoàn thành, chỉ chạy và tiếp tục
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
        // --- Kết thúc Logic RunOnStart mới ---

        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                TimeSpan delay = Scheduler.GetNextDelay(CurrentConfig);

                if (delay.TotalMilliseconds < 0)
                    delay = TimeSpan.Zero;
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
                break;

            try
            {
                await _parallelLimiter.WaitAsync(_cts.Token);
                _ = Task.Run(ExecuteJobLogicAsync, _cts.Token); // Chạy job
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
