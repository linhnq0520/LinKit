WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

//builder.Services.AddLinKitCqrs();
//builder.Services.AddGeneratedServices();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default);
});

//builder.Services.AddLinKitMessaging();
//builder.Services.AddLinKitRabbitMQ(builder.Configuration);
builder.Services.AddLogging();
builder.AddBackgroundJobs();
WebApplication app = builder.Build();

app.UseHttpsRedirection();
app.MapGeneratedEndpoints();

app.Run();
