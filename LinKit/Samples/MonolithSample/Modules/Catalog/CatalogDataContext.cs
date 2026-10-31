using LinKit.Data;
using MonolithSample.Modules.Catalog.Domain;

namespace MonolithSample.Modules.Catalog;

[DataContext(typeof(CategoryEntity), typeof(ProductEntity), typeof(TenantStoreEntity))]
public sealed partial class CatalogDataContext;
