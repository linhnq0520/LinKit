using LinKit.Core;
using LinKit.Grpc;
using LinKit.Messaging.Kafka;
using LinKit.Messaging.RabbitMQ;
using LinKit.Outbox;
using MicroserviceSample.OrderService.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// 1. Đăng ký CQRS Handlers & Mediator sinh bởi LinKit.Generator
builder.Services.AddLinKitCqrs();

// 2. Đăng ký Generated gRPC Clients Mediator & Channel Provider
builder.Services.AddLinKitGrpcClient();
builder.Services.AddSingleton<IGrpcChannelProvider, SimpleGrpcChannelProvider>();

// 3. Đăng ký Generated Message Publisher (IMessagePublisher) & Transactional Outbox
builder.Services.AddLinKitMessaging();
builder.Services.AddLinKitOutbox();

// 4. Linh hoạt cấu hình Broker: Chuyển đổi giữa RabbitMQ và Kafka chỉ qua cấu hình
var brokerType = builder.Configuration["Messaging:Broker"] ?? "RabbitMQ";
if (string.Equals(brokerType, "Kafka", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddLinKitKafka(builder.Configuration);
}
else
{
    builder.Services.AddLinKitRabbitMQ(builder.Configuration);
}

var app = builder.Build();

// 5. Tự động map các Minimal API [ApiEndpoint]
app.MapGeneratedEndpoints();

app.Run();
