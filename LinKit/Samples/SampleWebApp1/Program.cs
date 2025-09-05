using LinKit.Core;
using LinKit.Core.Grpc;
using SampleWebApp1.Features;
using SampleWebApp1.Infrastructures;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolver =
        JsonTypeInfoResolver.Combine(
            AppJsonSerializerContext.Default,
            new DefaultJsonTypeInfoResolver()
        );
});


builder.Services.AddLinKitCqrs().AddLinKitGrpcClient();
builder.Services.AddSingleton<IGrpcChannelProvider, ConfigurableGrpcChannelProvider>();
//builder.Services.AddLinKitMessaging();//.AddLinKitRabbitMQ(builder.Configuration);  // Đăng ký IMessagePublisher được sinh ra
var app = builder.Build();
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (Exception ex)
    {
        var logger = context.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("GlobalException");

        logger.LogError(ex,
            "Unhandled exception at {Path}", context.Request.Path);

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/json";

        await context.Response.WriteAsJsonAsync(new
        {
            error = ex.Message
        });
    }
});

app.MapGeneratedEndpoints();
app.Run();

[JsonSerializable(typeof(Contract.Models.UserDto))]
[JsonSerializable(typeof(GetUserQuery))]
[JsonSerializable(typeof(Contract.Models.GetUserById))]
internal partial class AppJsonSerializerContext : JsonSerializerContext
{

}
