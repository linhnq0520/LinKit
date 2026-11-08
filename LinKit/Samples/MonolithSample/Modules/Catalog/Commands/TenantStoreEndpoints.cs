using LinKit.Core.Cqrs;
using LinKit.Core.Endpoints;
using LinKit.Data;
using LinKit.Tenancy;

namespace MonolithSample.Modules.Catalog.Domain;

/// <summary>
/// Sample entity using the automatic multi-tenancy support of LinKit.Data and LinKit.Tenancy.
/// Because the type carries [MultiTenant], every query issued through DataSession appends
/// <c>WHERE tenant_id = @currentTenant</c> automatically, and inserts fill the tenant column.
/// </summary>
[Table("catalog_tenant_stores", Comment = "Stores isolated per tenant")]
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

// 1. Command that creates a store and assigns the current tenant automatically.
[HttpPost(
    "api/tenancy/stores",
    Summary = "Create a store and assign the current tenant automatically"
)]
public record CreateTenantStoreCommand(string StoreName) : ICommand<int>;

[CqrsHandler]
public class CreateTenantStoreCommandHandler(
    CatalogDataContext data,
    ITenantAccessor tenantAccessor
) : ICommandHandler<CreateTenantStoreCommand, int>
{
    public async Task<int> HandleAsync(
        CreateTenantStoreCommand request,
        CancellationToken ct = default
    )
    {
        var currentTenantId = tenantAccessor.Current?.TenantId ?? "tenant_default";

        var store = new TenantStoreEntity
        {
            StoreName = request.StoreName,
            TenantId = currentTenantId,
            IsActive = true,
        };

        return await data.TenantStoreEntity.InsertAsync(store, ct);
    }
}

// 2. Query that lists stores; LinKit filters them to the current tenant automatically.
[HttpGet("api/tenancy/stores", Summary = "List the stores that belong to the current tenant")]
public record GetMyTenantStoresQuery : IQuery<IReadOnlyList<TenantStoreEntity>>;

[CqrsHandler]
public class GetMyTenantStoresQueryHandler(CatalogDataContext data, ITenantAccessor tenantAccessor)
    : IQueryHandler<GetMyTenantStoresQuery, IReadOnlyList<TenantStoreEntity>>
{
    public async Task<IReadOnlyList<TenantStoreEntity>> HandleAsync(
        GetMyTenantStoresQuery request,
        CancellationToken ct = default
    )
    {
        var currentTenantId = tenantAccessor.Current?.TenantId ?? "tenant_default";

        // The tenant filter is applied by the framework; this predicate only narrows to active rows.
        return await data
            .TenantStoreEntity.Query()
            .Where(s => s.TenantId.Equal(currentTenantId) & s.IsActive.Equal(true))
            .ToListAsync(ct);
    }
}
