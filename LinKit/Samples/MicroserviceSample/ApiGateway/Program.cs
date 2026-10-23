using System.Threading.RateLimiting;
using LinKit.Core;
using LinKit.Grpc;
using MicroserviceSample.ApiGateway.Infrastructure;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// 1. Đăng ký CQRS Handlers & Mediator sinh bởi LinKit.Generator
builder.Services.AddLinKitCqrs();

// 2. Đăng ký gRPC Client Mediator sinh bởi LinKit.Grpc.Generator
builder.Services.AddLinKitGrpcClient();

// 3. Đăng ký Custom Channel Provider & Custom Interceptor Provider cho API Gateway
builder.Services.AddSingleton<CorrelationGrpcInterceptor>();
builder.Services.AddSingleton<IGrpcChannelProvider, GatewayGrpcChannelProvider>();
builder.Services.AddSingleton<IGrpcInterceptorProvider, GatewayInterceptorProvider>();

// 4. Cấu hình Rate Limiting & CORS cho Gateway
builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("GatewayRateLimit", opt =>
    {
        opt.PermitLimit = 500;
        opt.Window = TimeSpan.FromMinutes(1);
    });
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("GatewayCors", p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

var app = builder.Build();

app.UseCors("GatewayCors");
app.UseRateLimiter();

// 5. Tự động map tất cả các Gateway Endpoints [ApiEndpoint]
app.MapGeneratedEndpoints();

app.Run();
