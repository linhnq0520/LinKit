using LinKit.Data;
using MonolithSample.BuildingBlocks.Domain;

namespace MonolithSample.Modules.Catalog.Domain;

public enum ProductStatus
{
    Draft = 0,
    Active = 1,
    Archived = 2
}

[Table("catalog_products", Comment = "Catalog products inventory table")]
[CheckConstraint("Price >= 0", Name = "chk_product_price_positive")]
public class ProductEntity : BaseEntity
{
    [Indexed]
    [MaxLength(200)]
    [Comment("Product display name")]
    public string Name { get; set; } = string.Empty;

    [DecimalPrecision(18, 2)]
    public decimal Price { get; set; }

    [DefaultValueSql("0")]
    public int Stock { get; set; } = 0;

    [MaxLength(50)]
    public string? Sku { get; set; }

    public bool IsFeatured { get; set; } = false;

    public ProductStatus Status { get; set; } = ProductStatus.Active;

    public DateOnly? ReleasedDate { get; set; }

    public TimeOnly? DailyRestockTime { get; set; }

    [JsonColumn(typeof(MonolithDataJsonContext))]
    public List<string>? Tags { get; set; }

    [Column(Order = 100)]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
