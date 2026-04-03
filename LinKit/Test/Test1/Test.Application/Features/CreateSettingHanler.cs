using LinKit.Core.Cqrs;
using Test1.Infarstructure;

namespace Test.Application.Features
{
    //[CqrsHandler]
    public class CreateSettingHanler : ICommandHandler<CreateSetting, bool>
    {
        public Task<bool> HandleAsync(CreateSetting request, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }
    }
}
