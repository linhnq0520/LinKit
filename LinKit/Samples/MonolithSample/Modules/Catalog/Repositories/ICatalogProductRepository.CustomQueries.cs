namespace MonolithSample;

public partial interface IProductEntityRepository
{
    Task<long> CountInStockAsync(CancellationToken cancellationToken = default);
}
