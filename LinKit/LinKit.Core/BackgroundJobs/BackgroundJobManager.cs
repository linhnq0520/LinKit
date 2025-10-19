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
    private readonly Dictionary<string, JobRunner> _jobs = new();

    public BackgroundJobManager(IOptionsMonitor<BackgroundJobConfig> monitor, IServiceProvider sp)
    {
        _monitor = monitor;
        _sp = sp;
        _logger = sp.GetService<ILogger<BackgroundJobManager>>();
        _monitor.OnChange(OnConfigChanged);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        LoadJobs(_monitor.CurrentValue.BackgroundJobs);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (JobRunner job in _jobs.Values)
            job.Stop();
        return Task.CompletedTask;
    }

    private void LoadJobs(IEnumerable<JobConfig> configs)
    {
        foreach (JobConfig? config in configs.Where(c => c.IsActive))
        {
            if (!_jobs.ContainsKey(config.Name))
            {
                JobRunner runner = new JobRunner(config.Name, _sp, _monitor);
                _jobs[config.Name] = runner;
                runner.Start();
            }
        }
    }

    private void OnConfigChanged(BackgroundJobConfig newConfig)
    {
        List<JobConfig> newJobs = newConfig.BackgroundJobs;

        // Remove old jobs
        foreach (string? old in _jobs.Keys.Except(newJobs.Select(j => j.Name)).ToList())
        {
            _logger?.LogInformation("Stopping removed job {Job}", old);
            _jobs[old].Stop();
            _jobs.Remove(old);
        }

        // Restart changed jobs
        foreach (JobConfig config in newJobs)
        {
            if (_jobs.TryGetValue(config.Name, out JobRunner? runner))
            {
                JobConfig? old = _monitor.CurrentValue.BackgroundJobs.FirstOrDefault(j =>
                    j.Name == config.Name
                );
                if (old != null && !old.Equals(config))
                {
                    _logger?.LogInformation("Restarting changed job {Job}", config.Name);
                    runner.Stop();
                    runner = new JobRunner(config.Name, _sp, _monitor);
                    _jobs[config.Name] = runner;
                    runner.Start();
                }
            }
            else if (config.IsActive)
            {
                _logger?.LogInformation("Starting new job {Job}", config.Name);
                JobRunner runner1 = new JobRunner(config.Name, _sp, _monitor);
                _jobs[config.Name] = runner1;
                runner1.Start();
            }
        }
    }
}
