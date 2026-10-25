namespace MonolithSample.Modules.Catalog.Models;

public sealed record CategoryInventorySummaryDto(
    int? CategoryId,
    string? CategoryName,
    long ProductCount,
    decimal? TotalPrice);
