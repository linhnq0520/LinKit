namespace MonolithSample.Modules.Catalog.Models;

public record ProductDto(Guid Id, string Name, decimal Price, int Stock);
