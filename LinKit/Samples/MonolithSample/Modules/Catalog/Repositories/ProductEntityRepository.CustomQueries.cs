using LinKit.Data;

namespace MonolithSample;

public partial class ProductEntityRepository
{
    public Task<long> CountInStockAsync(CancellationToken cancellationToken = default) =>
        Query()
            .Where("Stock > 0")
            .CountAsync(cancellationToken);
}
