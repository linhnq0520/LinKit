namespace LinKit.Core.Cqrs;

public sealed class Mediator : IMediator
{
    public IServiceProvider Services { get; }

    public Mediator(IServiceProvider serviceProvider)
    {
        Services = serviceProvider;
    }
}