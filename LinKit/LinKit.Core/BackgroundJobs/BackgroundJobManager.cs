using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LinKit.Core.BackgroundJobs;

/// <summary>
/// Hosted service that owns the configured jobs. It reads <see cref="BackgroundJobConfig"/>, starts one
/// <see cref="JobRunner"/> per active job and reacts to configuration reloads by starting, restarting or stopping
/// individual jobs without touching the others. Register it with
/// <see cref="BackgroundJobServiceCollectionExtensions.AddBackgroundJobManager"/>.
/// </summary>
public class BackgroundJobManager : IHostedService, IBackgroundJobTrigger
{
    private readonly IOptionsMonitor<BackgroundJobConfig> _monitor;
    private readonly IServiceProvider _sp;
    private readonly ILogger<BackgroundJobManager>? _logger;
    private readonly Dictionary<string, JobRunner> _jobs = [];
    private readonly object _lock = new();

    /// <summary>
    /// Initializes the manager and subscribes to configuration changes.
    /// </summary>
    /// <param name="monitor">Source of the job configuration, monitored for reloads.</param>
    /// <param name="sp">Root provider used to create the scope of every execution.</param>
    public BackgroundJobManager(IOptionsMonitor<BackgroundJobConfig> monitor, IServiceProvider sp)
    {
        _monitor = monitor;
        _sp = sp;
        _logger = sp.GetService<ILogger<BackgroundJobManager>>();
        _monitor.OnChange(OnConfigChanged);
    }

    /// <summary>
    /// Starts a runner for every active job in the current configuration.
    /// </summary>
    /// <param name="cancellationToken">Host startup token.</param>
    /// <returns>A completed task; runners run in the background.</returns>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger?.LogInformation("BackgroundJobManager is starting.");
        LoadOrUpdateJobs(_monitor.CurrentValue);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops every runner, waiting up to five seconds for each one to observe cancellation.
    /// </summary>
    /// <param name="cancellationToken">Host shutdown token.</param>
    /// <returns>A completed task.</returns>
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

    /// <inheritdoc />
    public Task TriggerAsync(string jobName, CancellationToken cancellationToken = default) =>
        TriggerAsync(jobName, embeddedData: null, cancellationToken);

    /// <inheritdoc />
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
