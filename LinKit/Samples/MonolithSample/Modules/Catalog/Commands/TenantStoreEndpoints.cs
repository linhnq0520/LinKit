using LinKit.Core.Cqrs;
using LinKit.Core.Endpoints;
using LinKit.Data;
using LinKit.Tenancy;

namespace MonolithSample.Modules.Catalog.Domain;

/// <summary>
/// Demo Entity áp dụng Multi-Tenancy tự động của LinKit.Data & LinKit.Tenancy.
/// Khi có attribute [MultiTenant], mọi câu query qua DataSession sẽ tự động chèn WHERE tenant_id = @currentTenant!
/// </summary>
[Table("catalog_tenant_stores", Comment = "Gian hàng được cô lập theo Tenant")]
[MultiTenant("TenantId")]
public class TenantStoreEntity
{
    [PrimaryKey(AutoIncrement = true)]
    public int Id { get; set; }

    [MaxLength(100)]
    public string StoreName { get; set; } = string.Empty;

    [MaxLength(50)]
    public string TenantId { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;
}

// 1. Command tạo gian hàng
[HttpPost("api/tenancy/stores", Summary = "Tạo gian hàng mới tự động gán Tenant ID hiện tại")]
public record CreateTenantStoreCommand(string StoreName) : ICommand<int>;

[CqrsHandler]
public class CreateTenantStoreCommandHandler(CatalogDataContext data, ITenantAccessor tenantAccessor) 
    : ICommandHandler<CreateTenantStoreCommand, int>
{
    public async Task<int> HandleAsync(CreateTenantStoreCommand request, CancellationToken ct = default)
    {
        var currentTenantId = tenantAccessor.Context?.TenantId ?? "tenant_default";
        
        var store = new TenantStoreEntity
        {
            StoreName = request.StoreName,
            TenantId = currentTenantId,
            IsActive = true
        };

        return await data.TenantStoreEntity.InsertAsync(store, ct);
    }
}

// 2. Query lấy danh sách gian hàng (LinKit tự động lọc đúng tenant)
[HttpGet("api/tenancy/stores", Summary = "Lấy danh sách gian hàng thuộc Tenant hiện tại")]
public record GetMyTenantStoresQuery : IQuery<IReadOnlyList<TenantStoreEntity>>;

[CqrsHandler]
public class GetMyTenantStoresQueryHandler(CatalogDataContext data, ITenantAccessor tenantAccessor) 
    : IQueryHandler<GetMyTenantStoresQuery, IReadOnlyList<TenantStoreEntity>>
{
    public async Task<IReadOnlyList<TenantStoreEntity>> HandleAsync(GetMyTenantStoresQuery request, CancellationToken ct = default)
    {
        var currentTenantId = tenantAccessor.Context?.TenantId ?? "tenant_default";

        // Lọc theo Tenant ID hiện tại
        return await data.TenantStoreEntity.Query()
            .Where(s => s.TenantId.Equal(currentTenantId) & s.IsActive.Equal(true))
            .ToListAsync(ct);
    }
}
