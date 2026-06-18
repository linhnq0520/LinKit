using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LinKit.Core.BackgroundJobs;

public class BackgroundJobManager : IHostedService, IBackgroundJobTrigger
{
    private readonly IOptionsMonitor<BackgroundJobConfig> _monitor;
    private readonly IServiceProvider _sp;
    private readonly ILogger<BackgroundJobManager>? _logger;
    private readonly Dictionary<string, JobRunner> _jobs = [];
    private readonly object _lock = new();

    public BackgroundJobManager(IOptionsMonitor<BackgroundJobConfig> monitor, IServiceProvider sp)
    {
        _monitor = monitor;
        _sp = sp;
        _logger = sp.GetService<ILogger<BackgroundJobManager>>();
        _monitor.OnChange(OnConfigChanged);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger?.LogInformation("BackgroundJobManager is starting.");
        LoadOrUpdateJobs(_monitor.CurrentValue);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger?.LogInformation("BackgroundJobManager is stopping. Stopping all running jobs.");
        lock (_lock)
        {
            foreach (JobRunner job in _jobs.Values.ToList())
            {
                job.Stop();
            }
            _jobs.Clear();
        }
        return Task.CompletedTask;
    }

    public Task TriggerAsync(string jobName, CancellationToken cancellationToken = default) =>
        TriggerAsync(jobName, embeddedData: null, cancellationToken);

    public async Task TriggerAsync(
        string jobName,
        string? embeddedData,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobName);

        JobRunner? runner;
        lock (_lock)
        {
            _jobs.TryGetValue(jobName, out runner);
        }

        if (runner != null)
        {
            _logger?.LogInformation("Manually triggering active job [{Job}].", jobName);
            await runner.TriggerNowAsync(embeddedData, cancellationToken);
            return;
        }

        JobConfig? jobConfig = _monitor.CurrentValue.BackgroundJobs.FirstOrDefault(j =>
            string.Equals(j.Name, jobName, StringComparison.OrdinalIgnoreCase)
        );

        if (jobConfig == null)
        {
            throw new InvalidOperationException(
                $"Background job '{jobName}' was not found in configuration."
            );
        }

        _logger?.LogInformation(
            "Manually triggering job [{Job}] from configuration (not currently scheduled).",
            jobName
        );

        string data = embeddedData ?? jobConfig.EmbeddedData;
        await BackgroundJobExecution.ExecuteAsync(jobConfig, data, _sp, cancellationToken, _logger);
    }

    private void OnConfigChanged(BackgroundJobConfig newConfig)
    {
        _logger?.LogInformation("Configuration changed. Reloading background jobs.");
        LoadOrUpdateJobs(newConfig);
    }

    private void LoadOrUpdateJobs(BackgroundJobConfig config)
    {
        lock (_lock)
        {
            var activeJobsFromConfig = new Dictionary<string, JobConfig>(
                StringComparer.OrdinalIgnoreCase
            );
            foreach (var job in config.BackgroundJobs.Where(j => j.IsActive))
            {
                activeJobsFromConfig[job.Name] = job;
            }

            var runningJobNames = _jobs.Keys.ToList();
            foreach (var jobName in runningJobNames)
            {
                if (!activeJobsFromConfig.ContainsKey(jobName))
                {
                    _logger?.LogInformation(
                        "Stopping job {Job} because it was removed or deactivated.",
                        jobName
                    );
                    if (_jobs.TryGetValue(jobName, out var runnerToStop))
                    {
                        runnerToStop.Stop();
                        _jobs.Remove(jobName);
                    }
                }
            }

            foreach (var newJobConfig in activeJobsFromConfig.Values)
            {
                if (_jobs.TryGetValue(newJobConfig.Name, out var existingRunner))
                {
                    if (!existingRunner.CurrentConfig.Equals(newJobConfig))
                    {
                        _logger?.LogInformation(
                            "Restarting job {Job} due to configuration change.",
                            newJobConfig.Name
                        );
                        existingRunner.Stop();

                        var newRunner = new JobRunner(newJobConfig, _sp);
                        _jobs[newJobConfig.Name] = newRunner;
                        newRunner.Start();
                    }
                }
                else
                {
                    _logger?.LogInformation(
                        "Starting new or reactivated job {Job}.",
                        newJobConfig.Name
                    );
                    var newRunner = new JobRunner(newJobConfig, _sp);
                    _jobs[newJobConfig.Name] = newRunner;
                    newRunner.Start();
                }
            }
        }
    }
}
