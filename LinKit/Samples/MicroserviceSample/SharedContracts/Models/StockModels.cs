using LinKit.Core.Cqrs;
using LinKit.Grpc;
using MicroserviceSample.SharedContracts.Grpc;

namespace MicroserviceSample.SharedContracts.Models;

[GrpcClient(typeof(InventoryService.InventoryServiceClient), "CheckStockAsync")]
public partial class CheckStockQuery : IQuery<CheckStockResponse>
{
    public string ProductCode { get; set; } = string.Empty;
    public int Quantity { get; set; }
}
