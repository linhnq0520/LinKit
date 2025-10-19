using System.Text.Json;
using LinKit.Core.Cqrs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LinKit.Core.BackgroundJobs;

public class JobRunner
{
    private readonly string _jobName;
    private readonly IServiceProvider _serviceProvider;
    private readonly IOptionsMonitor<BackgroundJobConfig> _configMonitor;
    private readonly ILogger<JobRunner>? _logger;
    private readonly CancellationTokenSource _cts = new();
    private Task? _runnerTask;
    private SemaphoreSlim? _parallelLimiter;

    public JobRunner(
        string jobName,
        IServiceProvider sp,
        IOptionsMonitor<BackgroundJobConfig> monitor
    )
    {
        _jobName = jobName;
        _serviceProvider = sp;
        _configMonitor = monitor;
        _logger = sp.GetService<ILogger<JobRunner>>();
    }

    public void Start()
    {
        _runnerTask = Task.Run(RunAsync);
    }

    public void Stop()
    {
        _cts.Cancel();
        try
        {
            _runnerTask?.Wait(500);
        }
        catch (AggregateException ex) when (ex.InnerException is TaskCanceledException) { }
    }

    private async Task RunAsync()
    {
        _logger?.LogInformation("Job [{Job}] started.", _jobName);

        while (!_cts.Token.IsCancellationRequested)
        {
            JobConfig? config = _configMonitor.CurrentValue.BackgroundJobs.FirstOrDefault(x =>
                x.Name == _jobName
            );

            if (config == null || !config.IsActive)
            {
                _logger?.LogInformation("Job [{Job}] inactive or removed.", _jobName);
                break;
            }

            _parallelLimiter ??= new SemaphoreSlim(config.MaxParallel);

            try
            {
                await _parallelLimiter.WaitAsync(_cts.Token);

                _ = Task.Run(
                    async () =>
                    {
                        using IServiceScope scope = _serviceProvider.CreateScope();

                        IBackgroundJobMapper? backgroundJobMapper =
                            scope.ServiceProvider.GetService<IBackgroundJobMapper>();
                        JobInfo? jobInfo = backgroundJobMapper?.GetJobInfoByName(_jobName);
                        if (jobInfo == null)
                        {
                            _logger?.LogWarning("Job info not found for job: {Job}", _jobName);
                            return;
                        }

                        try
                        {
                            if (!string.IsNullOrWhiteSpace(config.EmbeddedData))
                            {
                                jobInfo.Instance = JsonSerializer.Deserialize(
                                    config.EmbeddedData,
                                    jobInfo.JobType!
                                );
                            }
                            IMediator mediator =
                                scope.ServiceProvider.GetRequiredService<IMediator>();
                            if (jobInfo.IsCommand)
                            {
                                await mediator.SendAsync((dynamic)jobInfo.Instance!, _cts.Token);
                            }
                            else
                            {
                                await mediator.QueryAsync((dynamic)jobInfo.Instance!, _cts.Token);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogError(ex, "Error executing job {Job}", _jobName);
                        }
                        finally
                        {
                            _parallelLimiter.Release();
                        }
                    },
                    _cts.Token
                );
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error scheduling job {Job}", _jobName);
            }

            await Task.Delay(TimeSpan.FromSeconds(config.TimeIntervalSeconds), _cts.Token);
        }

        _logger?.LogInformation("Job [{Job}] stopped.", _jobName);
    }
}
