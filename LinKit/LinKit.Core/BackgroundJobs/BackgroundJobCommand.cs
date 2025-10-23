using LinKit.Core.Cqrs;

namespace LinKit.Core.BackgroundJobs;

public abstract class BackgroundJobCommand : ICommand
{
    public string? EmbeddedData { get; set; }
}
