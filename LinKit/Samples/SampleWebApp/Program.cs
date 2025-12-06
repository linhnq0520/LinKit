using System.Threading.RateLimiting;
using SampleWebApp;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddLinKitCqrs();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default);
});
builder.Services.AddGrpc();

//builder.Services.AddLinKitMessaging();
//builder.Services.AddLinKitRabbitMQ(builder.Configuration);

builder.Services.AddLogging();
builder.AddBackgroundJobs();

// --- Swagger ---
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy(
        "HelloLimit",
        ctx =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 2,
                    Window = TimeSpan.FromSeconds(10),
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = 0,
                }
            )
    );
});

// ---------------

WebApplication app = builder.Build();

app.UseHttpsRedirection();

// --- Swagger UI ---
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// ------------------
app.UseRateLimiter();
app.MapGeneratedEndpoints();

//app.MapGrpcService<SampleWebApp.Grpc.Users.LinKitUserGrpcService>();

app.Run();
