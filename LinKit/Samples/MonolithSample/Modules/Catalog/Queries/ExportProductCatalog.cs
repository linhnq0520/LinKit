using System.Text;
using LinKit.Core.Cqrs;
using LinKit.Core.Endpoints;

namespace MonolithSample.Modules.Catalog.Queries;

[HttpGet("api/catalog/products/export-csv", Summary = "Xuất danh mục sản phẩm ra file CSV")]
public record ExportProductCatalogQuery : IQuery<FileResponse>;

[CqrsHandler]
public class ExportProductCatalogQueryHandler : IQueryHandler<ExportProductCatalogQuery, FileResponse>
{
    public Task<FileResponse> HandleAsync(ExportProductCatalogQuery request, CancellationToken cancellationToken = default)
    {
        var csvBuilder = new StringBuilder();
        csvBuilder.AppendLine("Id,ProductName,Price,Stock");
        csvBuilder.AppendLine("1,Bàn phím cơ Custom,1500000,20");
        csvBuilder.AppendLine("2,Chuột công thái học Không dây,890000,50");
        csvBuilder.AppendLine("3,Màn hình đồ họa 4K 27 inch,8500000,10");

        byte[] bytes = Encoding.UTF8.GetBytes(csvBuilder.ToString());

        var fileResponse = FileResponse.FromBytes(
            bytes,
            contentType: "text/csv; charset=utf-8",
            fileName: "product_catalog.csv"
        );

        return Task.FromResult(fileResponse);
    }
}
