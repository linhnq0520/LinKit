using System.Text.Json;
using LinKit.Core.Cqrs;
using LinKit.Core.Endpoints;
using LinKit.Json;

namespace MonolithSample.Modules.Catalog.Queries;

/// <summary>
/// Demo use-case LinKit.Json:
/// Trích xuất dữ liệu động từ chuỗi JSON phức tạp bằng JsonPath mà không cần deserialize thành class (Zero-Allocation)!
/// </summary>
[HttpPost("api/json/extract-path", Summary = "Trích xuất giá trị JSON cực nhanh bằng LinKit.Json Path Navigator")]
public record ExtractJsonPathRequest(string JsonPayload, string Path) : ICommand<string>;

[CqrsHandler]
public class ExtractJsonPathRequestHandler : ICommandHandler<ExtractJsonPathRequest, string>
{
    public Task<string> HandleAsync(ExtractJsonPathRequest request, CancellationToken ct = default)
    {
        try
        {
            using var doc = JsonDocument.Parse(request.JsonPayload);
            var root = doc.RootElement;

            // Dùng LinKit.Json TrySelect & GetValueByPath để lấy giá trị theo Path (ví dụ: "order.items[0].price")
            if (root.TrySelect(request.Path, out JsonElement element))
            {
                return Task.FromResult(element.ToString());
            }

            return Task.FromResult("<Path not found>");
        }
        catch (Exception ex)
        {
            return Task.FromResult($"Error: {ex.Message}");
        }
    }
}
