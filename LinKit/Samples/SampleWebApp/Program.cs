using LinKit.Core.Endpoints;
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

app.MapGeneratedEndpoints();
app.MapGrpcService<SampleWebApp.Grpc.Users.LinKitUserGrpcService>();

app.Run();
