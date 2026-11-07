using System.Threading.RateLimiting;
using LinKit.Caching;
using LinKit.Data;
using LinKit.Outbox;
using LinKit.Resilience;
using LinKit.Sagas;
using LinKit.Tenancy;
using Microsoft.AspNetCore.RateLimiting;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// 1. Đăng ký toàn bộ CQRS Handlers, Behaviors và Mediator sinh bởi LinKit.Generator
builder.Services.AddLinKitCqrs();

// 2. Tự động đăng ký các dịch vụ gắn [RegisterService] (ví dụ: IPricingService)
builder.Services.AddLinKitDependency();

// 3. Đăng ký Caching với tùy chọn linh hoạt: In-Memory hoặc Hybrid (L1 Memory + L2 Redis)
var cacheProvider = builder.Configuration.GetValue<string>("Cache:Provider") ?? "Memory";

if (string.Equals(cacheProvider, "Redis", StringComparison.OrdinalIgnoreCase))
{
    var redisConfig = builder.Configuration.GetSection("Redis");
    var redisOptions = new LinKitRedisOptions
    {
        Enabled = redisConfig.GetValue("Enabled", true),
        Host = redisConfig.GetValue("Host", "localhost")!,
        Port = redisConfig.GetValue("Port", 6379),
        Password = redisConfig.GetValue<string?>("Password"),
        Database = redisConfig.GetValue("Database", 0),
        DefaultTtl = TimeSpan.FromMinutes(redisConfig.GetValue("DefaultTtlMinutes", 30)),
    };

    builder.Services.AddLinKitRedisCaching(redisOptions);
}
else
{
    // Mặc định: In-Memory Provider
    builder.Services.AddLinKitCaching();
}

// Đăng ký context JSON cho Serialization (an toàn cho Native AOT & Redis caching)
LinKit.Core.Messaging.LinKitMessageJson.AddContext(MonolithSample.MonolithDataJsonContext.Default);

builder.Services.AddLinKitResilience();

// 4. Đăng ký Tenancy (Multi-Tenancy với AsyncLocal), Outbox và Sagas
builder.Services.AddLinKitTenancy();
builder.Services.AddLinKitOutbox();
builder.Services.AddLinKitSagas<MonolithSample.Modules.Ordering.Commands.OrderSagaState>(
    // Sample chỉ dùng repository in-memory; production thật phải đăng ký ISagaRepository bền vững
    // trước AddLinKitSagas, nếu không việc resolve sẽ ném lỗi khi môi trường là Production.
    options => options.AllowInMemoryInProduction = true
);

// 4. Đăng ký LinKit.Data - Zero-Reflection ORM với Auto-Schema Synchronization (Zero-Migration)
builder.Services.AddLinKitData(options =>
{
    var connectionString =
        builder.Configuration.GetConnectionString("DefaultConnection")
        ?? "Server=localhost,1433;Database=MonolithSampleDb;User Id=sa;Password=Iinh2912@;TrustServerCertificate=True;";
    options.UseConnection(
        DatabaseDialect.SqlServer,
        () => new Microsoft.Data.SqlClient.SqlConnection(connectionString)
    );
    options.EnsureDatabaseCreated = true; // Tự động tạo Database nếu chưa tồn tại trên Server!
    // options.AdminConnectionString = "Server=localhost,1433;Database=master;User Id=sa;Password=linh2912@;TrustServerCertificate=True;"; // Dùng nếu tài khoản chính không có quyền tạo DB
    // options.ConfigureUserProvisioning(user =>
    // {
    //     user.Enabled = true; // Tự động tạo user riêng và cấp quyền db_owner nếu được kích hoạt
    //     user.Username = "monolith_user";
    //     user.Password = "Monolith@123";
    //     user.Roles = ["db_owner"];
    // });
    options.AutoSyncSchema = true; // Tự động tạo bảng và thêm cột khi model thay đổi!
    options.RunMigrations = true; // Tự động chạy versioned data migration!
    options.UseJsonSerializerContext(MonolithSample.MonolithDataJsonContext.Default); // 100% Native AOT & Trimming-Safe JSON!
});

// 5. Đăng ký Background Job Manager tự động chạy tác vụ ngầm theo JSON config
builder.AddBackgroundJobs("BackgroundJobsConfig.json");

// 5. Cấu hình Rate Limiting & CORS Policies hỗ trợ [ApiEndpoint]
builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter(
        "FixedWindow",
        opt =>
        {
            opt.PermitLimit = 100;
            opt.Window = TimeSpan.FromMinutes(1);
            opt.QueueLimit = 10;
            opt.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        }
    );
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

// 6. Cấu hình Swagger / OpenAPI Documentation & Swagger UI
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

WebApplication app = builder.Build();

// Kích hoạt Swagger Middleware và Swagger UI
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "LinKit Monolith API v1");
});

// Điều hướng mặc định từ root (/) về /swagger để tiện kiểm thử ngay khi chạy
app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

app.UseCors("AllowAll");
app.UseRateLimiter();

// 7. Tự động map tất cả các endpoints [ApiEndpoint] kèm Route, Auth, Policy, OpenAPI metadata
app.MapGeneratedEndpoints();

app.Run();
