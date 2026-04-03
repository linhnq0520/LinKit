using LinKit.Core.Cqrs;

namespace Test1.Infarstructure
{
    public class InfraCommand : ICommand<bool>
    {

    }

    public class IfraHanlder : ICommandHandler<InfraCommand, bool>
    {
        Task<bool> IHandler<InfraCommand, bool>.HandleAsync(InfraCommand request, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }
    }
}
