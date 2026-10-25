using LinKit.Data;

namespace MonolithSample.Modules.Catalog.Models;

[DataRecord]
public sealed class ProductSummaryDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
}
