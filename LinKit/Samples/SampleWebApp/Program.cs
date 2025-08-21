
using LinKit.Core.Cqrs;
using Microsoft.AspNetCore.Mvc;
using SampleWebApp;
using SampleWebApp.Features.Users;
using LinKit.Core;

var builder = WebApplication.CreateBuilder(args);


builder.Services.AddLinKitCqrs();
builder.Services.AddGeneratedServices();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default);
});
var app = builder.Build();


app.UseHttpsRedirection();

app.MapGet("/users/{id:int}", async (int id, [FromServices] IMediator mediator) =>
{
    var query = new GetUserQuery(id);
    var user = await mediator.QueryAsync(query);
    return Results.Ok(user);
});

app.MapPost("/create-user", async ([FromBody] CreateUserCommand createUserCommand, [FromServices] IMediator mediator) =>
{
    var user = await mediator.SendAsync(createUserCommand);
    return Results.Ok(user);
});

app.Run();