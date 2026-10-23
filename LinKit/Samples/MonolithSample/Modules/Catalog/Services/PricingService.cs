using LinKit.Core.Abstractions;

namespace MonolithSample.Modules.Catalog.Services;

public interface IPricingService
{
    decimal CalculateDiscountedPrice(decimal originalPrice, decimal discountPercentage);
}

// LinKit Dependency Injection Generator tự động đăng ký:
// services.AddScoped<IPricingService, PricingService>();
[AddScoped]
public class PricingService : IPricingService
{
    public decimal CalculateDiscountedPrice(decimal originalPrice, decimal discountPercentage)
    {
        if (discountPercentage <= 0) return originalPrice;
        return originalPrice * (1 - (discountPercentage / 100m));
    }
}
