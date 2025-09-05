using SampleWebApp;
using LinKit.Core;

var builder = WebApplication.CreateBuilder(args);


//builder.Services.AddLinKitCqrs();
//builder.Services.AddGeneratedServices();
builder.Services.AddLinKitCqrs().AddLinKitDependency();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default);
});
builder.Services.AddGrpc();
var app = builder.Build();


app.UseHttpsRedirection();
app.MapGeneratedEndpoints();

app.MapGrpcService<SampleWebApp.Grpc.Users.LinKitUserGrpcService>();

app.Run();