using LinKit.Core.Cqrs;
using LinKit.Core.Results;
using LinKit.Grpc;
using MicroserviceSample.SharedContracts.Grpc;

namespace MicroserviceSample.SharedContracts.Models;

[GrpcClient(typeof(InventoryService.InventoryServiceClient), "CheckStockAsync")]
public partial class CheckStockQuery : IQuery<CheckStockResponse>
{
    public string ProductCode { get; set; } = string.Empty;
    public int Quantity { get; set; }
}

/// <summary>
/// The fail-soft variant of <see cref="CheckStockQuery"/>: a transport failure becomes a failed
/// <see cref="Result{T}"/> (status code preserved in <c>ErrorCode</c>) and the request carries its own deadline.
/// </summary>
[GrpcClient(typeof(InventoryService.InventoryServiceClient), "CheckStockAsync")]
public partial class TryCheckStockQuery : IQuery<Result<CheckStockResponse>>, IHasDeadline
{
    public string ProductCode { get; set; } = string.Empty;
    public int Quantity { get; set; }

    /// <inheritdoc />
    public TimeSpan? Deadline { get; set; } = TimeSpan.FromSeconds(5);
}
