#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LinKit.Core.Cqrs;
using LinKit.Core.Endpoints;
using Microsoft.AspNetCore.Http;

// Assembly-level Route Group with Filters
[assembly: ApiRouteGroup(
    "/api/v2/orders",
    Tag = "Orders V2",
    RequireAuthorization = false,
    Filters = [typeof(SampleWebApp1.Features.OrderGroupTelemetryFilter)]
)]

namespace SampleWebApp1.Features;

#region Test Filters

public class LoggingEndpointFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        Console.WriteLine($"[Filter:Logging] Path: {context.HttpContext.Request.Path}");
        return await next(context);
    }
}

public class AuditEndpointFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        Console.WriteLine($"[Filter:Audit] Executing audit check");
        return await next(context);
    }
}

public class OrderGroupTelemetryFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        Console.WriteLine($"[Filter:OrderGroupTelemetry] Order endpoint hit: {context.HttpContext.Request.Path}");
        return await next(context);
    }
}

public class AuditEnricherEndpointFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var result = await next(context);
        if (result is List<AuditItemDto> list)
        {
            var enriched = new List<AuditItemExtendDto>();
            foreach (var item in list)
            {
                enriched.Add(new AuditItemExtendDto(item.Id, item.Action, item.UserId, "Admin_Linh"));
            }
            return enriched;
        }
        return result;
    }
}

#endregion

#region Models & DTOs

public record TestResultDto(string Status, DateTime Timestamp);

public record AuditItemDto(int Id, string Action, string UserId);

public record AuditItemExtendDto(int Id, string Action, string UserId, string UserName)
    : AuditItemDto(Id, Action, UserId);

public record ItemCreatedDto(int Id, string Code);

public record ProductDto(int Id, string Name, decimal Price);

public record OrderDto(int Id, string OrderNumber, decimal TotalAmount);

#endregion

#region Case 1: Multiple Ordered Endpoint Filters
// Verifies: [EndpointFilter(..., Order = 1)], [EndpointFilter(..., Order = 2)]
[ApiEndpoint(ApiMethod.Get, "test/filtered-data", Summary = "Endpoint with ordered filters")]
[EndpointFilter(typeof(LoggingEndpointFilter), Order = 1)]
[EndpointFilter(typeof(AuditEndpointFilter), Order = 2)]
public record GetFilteredDataQuery : IQuery<TestResultDto>;

[CqrsHandler]
public class GetFilteredDataQueryHandler : IQueryHandler<GetFilteredDataQuery, TestResultDto>
{
    public Task<TestResultDto> HandleAsync(GetFilteredDataQuery query, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new TestResultDto("Success", DateTime.UtcNow));
    }
}
#endregion

#region Case 2: Custom ResponseType Override (Swagger Schema Enrichment)
// Verifies: ResponseType = typeof(List<AuditItemExtendDto>), producing extended model while CQRS returns base model
[ApiEndpoint(
    ApiMethod.Post,
    "test/audits",
    ResponseType = typeof(List<AuditItemExtendDto>),
    Summary = "Get audits with enriched usernames in Swagger"
)]
[EndpointFilter(typeof(AuditEnricherEndpointFilter))]
public record GetAuditListQuery : IQuery<List<AuditItemDto>>;

[CqrsHandler]
public class GetAuditListQueryHandler : IQueryHandler<GetAuditListQuery, List<AuditItemDto>>
{
    public Task<List<AuditItemDto>> HandleAsync(GetAuditListQuery query, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new List<AuditItemDto>
        {
            new(1, "CreateUser", "usr_001"),
            new(2, "UpdateRole", "usr_002")
        });
    }
}
#endregion

#region Case 3: 201 Created Status Code with Additional Produces Codes
// Verifies: SuccessStatusCode = 201, ProducesStatusCodes = [400, 409]
[ApiEndpoint(
    ApiMethod.Post,
    "test/items",
    SuccessStatusCode = StatusCodes.Status201Created,
    ProducesStatusCodes = [StatusCodes.Status400BadRequest, StatusCodes.Status409Conflict],
    Summary = "Create item returning 201 Created"
)]
public record CreateItemCommand(string Code, string Name) : ICommand<ItemCreatedDto>;

[CqrsHandler]
public class CreateItemCommandHandler : ICommandHandler<CreateItemCommand, ItemCreatedDto>
{
    public Task<ItemCreatedDto> HandleAsync(CreateItemCommand command, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ItemCreatedDto(1001, command.Code));
    }
}
#endregion

#region Case 4: 204 NoContent on Void Command
// Verifies: Void command producing 204 NoContent
[ApiEndpoint(
    ApiMethod.Delete,
    "test/items/{id}",
    SuccessStatusCode = StatusCodes.Status204NoContent,
    ProducesStatusCodes = [StatusCodes.Status404NotFound],
    Summary = "Delete item returning 204 NoContent"
)]
public record DeleteItemCommand([FromRoute] int Id) : ICommand;

[CqrsHandler]
public class DeleteItemCommandHandler : ICommandHandler<DeleteItemCommand>
{
    public Task<Unit> HandleAsync(DeleteItemCommand command, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Unit.Value);
    }
}
#endregion

#region Case 5: POST/PUT with AsParameters (Mixed Route + Body Params)
// Verifies: AsParameters = true allowing [FromRoute] and body properties in PUT
[ApiEndpoint(
    ApiMethod.Put,
    "test/products/{id}",
    AsParameters = true,
    Summary = "Update product with route parameter and body"
)]
public record UpdateProductCommand : ICommand<ProductDto>
{
    [FromRoute]
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
}

[CqrsHandler]
public class UpdateProductCommandHandler : ICommandHandler<UpdateProductCommand, ProductDto>
{
    public Task<ProductDto> HandleAsync(UpdateProductCommand command, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ProductDto(command.Id, command.Name, command.Price));
    }
}
#endregion

#region Case 6: Route Group & Group-Level Filter Inheritance
// Verifies: Group prefix deduplication and inherited OrderGroupTelemetryFilter
[ApiEndpoint(
    ApiMethod.Get,
    "/api/v2/orders/{id}",
    Group = "/api/v2/orders",
    Summary = "Get order by id under route group with group filter"
)]
public record GetOrderByIdQuery([FromRoute] int Id) : IQuery<OrderDto>;

[CqrsHandler]
public class GetOrderByIdQueryHandler : IQueryHandler<GetOrderByIdQuery, OrderDto>
{
    public Task<OrderDto> HandleAsync(GetOrderByIdQuery query, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new OrderDto(query.Id, $"ORD-{query.Id:D6}", 1500000m));
    }
}
#endregion

#region Case 7: Deprecated & Custom OperationId
// Verifies: Deprecated = true, OperationId = "ExecuteLegacyV1Query"
[ApiEndpoint(
    ApiMethod.Get,
    "test/legacy-report",
    Deprecated = true,
    OperationId = "ExecuteLegacyV1Query",
    Summary = "Deprecated legacy report API"
)]
public record LegacyReportQuery : IQuery<string>;

[CqrsHandler]
public class LegacyReportQueryHandler : IQueryHandler<LegacyReportQuery, string>
{
    public Task<string> HandleAsync(LegacyReportQuery query, CancellationToken cancellationToken = default)
    {
        return Task.FromResult("Legacy Report Data");
    }
}
#endregion

#region Case 8: Exclude From OpenAPI Description (Internal Endpoint)
// Verifies: ExcludeFromDescription = true
[ApiEndpoint(
    ApiMethod.Get,
    "test/internal-metrics",
    ExcludeFromDescription = true,
    Summary = "Hidden internal metrics"
)]
public record InternalMetricsQuery : IQuery<string>;

[CqrsHandler]
public class InternalMetricsQueryHandler : IQueryHandler<InternalMetricsQuery, string>
{
    public Task<string> HandleAsync(InternalMetricsQuery query, CancellationToken cancellationToken = default)
    {
        return Task.FromResult("CPU: 12%, Memory: 45MB");
    }
}
#endregion

#region Case 9: Security Attributes (Roles, Policies, RateLimit)
// Verifies: Roles, RateLimitPolicy, Policies in generator
[ApiEndpoint(
    ApiMethod.Post,
    "test/admin-maintenance",
    Roles = "Admin,SuperAdmin",
    RateLimitPolicy = "StrictLimit",
    Summary = "Secured administrative action"
)]
public record AdminMaintenanceCommand(string ActionName) : ICommand<bool>;

[CqrsHandler]
public class AdminMaintenanceCommandHandler : ICommandHandler<AdminMaintenanceCommand, bool>
{
    public Task<bool> HandleAsync(AdminMaintenanceCommand command, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(true);
    }
}
#endregion

#region Case 10: AllowAnonymous Override
// Verifies: AllowAnonymous = true
[ApiEndpoint(
    ApiMethod.Get,
    "test/public-ping",
    AllowAnonymous = true,
    Summary = "Public ping endpoint"
)]
public record PublicPingQuery : IQuery<string>;

[CqrsHandler]
public class PublicPingQueryHandler : IQueryHandler<PublicPingQuery, string>
{
    public Task<string> HandleAsync(PublicPingQuery query, CancellationToken cancellationToken = default)
    {
        return Task.FromResult("pong");
    }
}
#endregion
