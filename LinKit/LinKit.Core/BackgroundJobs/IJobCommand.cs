using LinKit.Core.Cqrs;

namespace LinKit.Core.BackgroundJobs
{
    public abstract class BackgroundJobCommand : ICommand
    {
        public string? EmbededData { get; set; }
    }
}
