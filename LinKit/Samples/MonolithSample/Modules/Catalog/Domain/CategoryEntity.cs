using MonolithSample.BuildingBlocks.Domain;
using LinKit.Data;

namespace MonolithSample.Modules.Catalog.Domain;

[Table("catalog_categories", Comment = "Product categories")]
public class CategoryEntity : BaseEntity
{
    [Indexed]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;
}
