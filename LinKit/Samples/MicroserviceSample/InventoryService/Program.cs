using LinKit.Core;
using LinKit.Grpc;
using LinKit.Messaging.Kafka;
using LinKit.Messaging.RabbitMQ;
using SharedContracts.Generated;
using LinKit.Outbox;

var builder = WebApplication.CreateBuilder(args);

// 1. Thêm gRPC Server runtime
builder.Services.AddGrpc();

// 2. Đăng ký CQRS Handlers & Mediator sinh bởi LinKit.Generator
builder.Services.AddLinKitCqrs();

// 3. Đăng ký gRPC Server Services sinh bởi LinKit.Grpc.Generator
builder.Services.AddLinKitGrpcServer();

// 4. Đăng ký Messaging Consumer Service sinh tự động bởi LinKit.Generator & Idempotent Inbox/Outbox
builder.Services.AddLinKitMessaging();
builder.Services.AddLinKitOutbox();

// 5. Linh hoạt cấu hình Broker: Chuyển đổi giữa RabbitMQ và Kafka chỉ qua cấu hình
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

// 6. Tự động map các gRPC Service endpoints
app.MapLinKitGrpcServices();

app.Run();
