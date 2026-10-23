using LinKit.Core.BackgroundJobs;
using LinKit.Core.Cqrs;
using Microsoft.Extensions.Logging;

namespace MonolithSample.Modules.Catalog.Jobs;

[BackgroundJob("InventoryAuditJob")]
public class InventoryAuditJobCommand : BackgroundJobCommand { }

[CqrsHandler]
public class InventoryAuditJobCommandHandler(ILogger<InventoryAuditJobCommandHandler> logger)
    : ICommandHandler<InventoryAuditJobCommand>
{
    public Task<Unit> HandleAsync(InventoryAuditJobCommand request, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("[BackgroundJob] Đang tự động kiểm kê và đồng bộ số lượng tồn kho định kỳ...");
        return Task.FromResult(Unit.Value);
    }
}
