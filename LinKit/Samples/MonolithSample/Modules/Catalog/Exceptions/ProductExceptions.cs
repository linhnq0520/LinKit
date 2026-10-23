using LinKit.Core.Endpoints;
using Microsoft.AspNetCore.Http;

namespace MonolithSample.Modules.Catalog.Exceptions;

[ApiExceptionMapping(StatusCodes.Status400BadRequest, Title = "Sản phẩm hết hàng", IncludeDetails = true)]
public class ProductOutOfStockException : Exception
{
    public ProductOutOfStockException(string message) : base(message) { }
}

[ApiExceptionMapping(StatusCodes.Status404NotFound, Title = "Sản phẩm không tồn tại", IncludeDetails = true)]
public class ProductNotFoundException : Exception
{
    public ProductNotFoundException(Guid productId) : base($"Không tìm thấy sản phẩm với ID: {productId}") { }
}
