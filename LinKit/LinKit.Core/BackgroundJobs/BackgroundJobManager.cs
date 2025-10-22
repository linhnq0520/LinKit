using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LinKit.Core.BackgroundJobs;

public class BackgroundJobManager : IHostedService
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

    private void OnConfigChanged(BackgroundJobConfig newConfig)
    {
        _logger?.LogInformation("Configuration changed. Reloading background jobs.");
        LoadOrUpdateJobs(newConfig);
    }

    private void LoadOrUpdateJobs(BackgroundJobConfig config)
    {
        lock (_lock)
        {
            _logger?.LogInformation(
                "Loaded {Count} jobs from configuration.",
                config.BackgroundJobs.Count
            );
            foreach (var job in config.BackgroundJobs)
            {
                _logger?.LogInformation(
                    "Job config: {JobName}, Active={IsActive}",
                    job.Name,
                    job.IsActive
                );
            }

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
