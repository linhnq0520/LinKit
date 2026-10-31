using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace LinKit.Generator.Generators;

internal record MessageInfo(
    string MessageType,
    string SimpleTypeName,
    string TopicOrExchange,
    string RoutingKey,
    string? QueueName,
    int MaxRetries,
    int RetryIntervalMs,
    string? DeadLetterQueue,
    bool IsNotification,
    bool IsCommand
);

internal static class MessagingGeneratorPart
{
    private const string MessageAttributeName = "LinKit.Core.Messaging.MessageAttribute";

    public static IncrementalValueProvider<IReadOnlyList<MessagingServiceInfo>> GetServices(
        IncrementalGeneratorInitializationContext context,
        IncrementalValueProvider<string> generatedNamespace
    )
    {
        IncrementalValueProvider<ImmutableArray<MessageInfo?>> collectedMessages = context
            .SyntaxProvider.ForAttributeWithMetadataName(
                MessageAttributeName,
                predicate: (node, _) => node is ClassDeclarationSyntax or RecordDeclarationSyntax,
                transform: (ctx, _) => GetMessageInfo(ctx)
            )
            .Where(info => info is not null)!
            .Collect();

        return collectedMessages.Combine(generatedNamespace).Select(
            (data, _) =>
            {
                var messages = data.Left;
                var namespaceName = data.Right;
                var services = new List<MessagingServiceInfo>();
                if (!messages.Any())
                {
                    return (IReadOnlyList<MessagingServiceInfo>)services;
                }

                services.Add(
                    new MessagingServiceInfo(
                        $"services.AddSingleton<LinKit.Core.Messaging.IMessagePublisher, global::{namespaceName}.GeneratedMessagePublisher>();"
                    )
                );

                var messagesByQueue = messages
                    .Where(m => !string.IsNullOrEmpty(m?.QueueName))
                    .GroupBy(m => m?.QueueName);

                foreach (var queueGroup in messagesByQueue)
                {
                    var queueName = queueGroup.Key!;
                    var className =
                        $"global::{namespaceName}.{new string(queueName.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray())}ConsumerService";

                    services.Add(
                        new MessagingServiceInfo(
                            $"services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService, {className}>();"
                        )
                    );
                }

                return services;
            }
        );
    }

    public static void Initialize(
        IncrementalGeneratorInitializationContext context,
        IncrementalValueProvider<string> generatedNamespace)
    {
        IncrementalValuesProvider<MessageInfo?> messageDeclarations = context
            .SyntaxProvider.ForAttributeWithMetadataName(
                MessageAttributeName,
                predicate: (node, _) => node is ClassDeclarationSyntax or RecordDeclarationSyntax,
                transform: (ctx, _) => GetMessageInfo(ctx)
            )
            .Where(info => info is not null);

        context.RegisterSourceOutput(
            messageDeclarations.Collect().Combine(generatedNamespace),
            (spc, data) =>
            {
                var messages = data.Left;
                var validMessages = messages.OfType<MessageInfo>().ToList();
                if (!validMessages.Any())
                {
                    return;
                }

                var publisherSource = GeneratePublisher(validMessages, data.Right);
                spc.AddSource(
                    "LinKit.Core.Messaging.Publisher.g.cs",
                    SourceText.From(publisherSource, Encoding.UTF8)
                );

                var consumerSource = GenerateConsumers(validMessages, data.Right);
                spc.AddSource(
                    "LinKit.Core.Messaging.Consumers.g.cs",
                    SourceText.From(consumerSource, Encoding.UTF8)
                );
            }
        );
    }

    private static MessageInfo? GetMessageInfo(GeneratorAttributeSyntaxContext context)
    {
        if (context.TargetSymbol is not INamedTypeSymbol messageSymbol)
        {
            return null;
        }

        var attributeData = context.Attributes[0];
        var topic = attributeData.ConstructorArguments[0].Value as string ?? string.Empty;
        var routingKey =
            attributeData.NamedArguments.FirstOrDefault(kvp => kvp.Key == "RoutingKey").Value.Value
                as string
            ?? messageSymbol.Name;
        var queueName =
            attributeData.NamedArguments.FirstOrDefault(kvp => kvp.Key == "QueueName").Value.Value
            as string;
        var maxRetries =
            attributeData.NamedArguments.FirstOrDefault(kvp => kvp.Key == "MaxRetries").Value.Value
            as int? ?? 0;
        var retryIntervalMs =
            attributeData.NamedArguments.FirstOrDefault(kvp => kvp.Key == "RetryIntervalMs").Value.Value
            as int? ?? 1000;
        var deadLetterQueue =
            attributeData.NamedArguments.FirstOrDefault(kvp => kvp.Key == "DeadLetterQueue").Value.Value
            as string;

        var isNotification = messageSymbol.AllInterfaces.Any(i => i.ToDisplayString().Contains("INotification"));
        var isCommand = messageSymbol.AllInterfaces.Any(i => i.ToDisplayString().Contains("ICommand"));

        return new MessageInfo(
            MessageType: messageSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            SimpleTypeName: messageSymbol.Name,
            TopicOrExchange: topic,
            RoutingKey: routingKey,
            QueueName: queueName,
            MaxRetries: maxRetries,
            RetryIntervalMs: retryIntervalMs,
            DeadLetterQueue: deadLetterQueue,
            IsNotification: isNotification,
            IsCommand: isCommand
        );
    }

    private static string GeneratePublisher(IReadOnlyList<MessageInfo> messages, string generatedNamespace)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            @"// <auto-generated> by LinKit.Generator
#nullable enable
using LinKit.Core.Abstractions;
using LinKit.Core.Messaging;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Threading;
using System.Threading.Tasks;

"
        );
        sb.AppendLine($"namespace {generatedNamespace}");
        sb.AppendLine("{");
        sb.AppendLine(
            @"    public sealed class GeneratedMessagePublisher : IMessagePublisher
    {
        private readonly IBrokerProducer _producer;
        private readonly IMessageMetadataProvider? _metadataProvider;

        public GeneratedMessagePublisher(IBrokerProducer producer, IServiceProvider serviceProvider)
        {
            _producer = producer;
            _metadataProvider = serviceProvider.GetService<IMessageMetadataProvider>();
        }
"
        );

        // Strongly-typed direct publish overloads
        foreach (var msg in messages.Distinct())
        {
            sb.AppendLine(
                $@"
        public Task PublishAsync({msg.MessageType} message, CancellationToken cancellationToken = default)
        {{
            if (message is null) return Task.CompletedTask;
            var headers = _metadataProvider?.GetHeaders(message);
            return _producer.ProduceAsync(""{msg.TopicOrExchange}"", ""{msg.RoutingKey}"", message, headers, cancellationToken);
        }}"
            );
        }

        // Generic fallback publish
        sb.AppendLine(
            @"
        public Task PublishAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default)
        {
            if (message is null) return Task.CompletedTask;
            var headers = _metadataProvider?.GetHeaders(message);

            return (object)message switch
            {"
        );

        foreach (var msg in messages)
        {
            sb.AppendLine(
                $@"                {msg.MessageType} m => _producer.ProduceAsync(""{msg.TopicOrExchange}"", ""{msg.RoutingKey}"", m, headers, cancellationToken),"
            );
        }

        sb.AppendLine(
            @"                _ => throw new System.InvalidOperationException($""Message type {typeof(TMessage).FullName} is not decorated with [Message] attribute."")
            };
        }
    }
}"
        );
        return sb.ToString();
    }

    private static string GenerateConsumers(IReadOnlyList<MessageInfo> messages, string generatedNamespace)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            @"// <auto-generated> by LinKit.Generator
#nullable enable
using LinKit.Core.Abstractions;
using LinKit.Core.Cqrs;
using LinKit.Core.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

"
        );
        sb.AppendLine($"namespace {generatedNamespace}");
        sb.AppendLine("{");

        var messagesByQueue = messages
            .Where(m => !string.IsNullOrEmpty(m.QueueName))
            .GroupBy(m => m.QueueName);

        foreach (var queueGroup in messagesByQueue)
        {
            var queueName = queueGroup.Key!;
            var className =
                $"{new string(queueName.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray())}ConsumerService";

            sb.AppendLine(
                $@"
    public sealed class {className} : BackgroundService
    {{
        private readonly IServiceProvider _serviceProvider;
        private readonly IBrokerConnection _connection;
        private readonly ILogger<{className}>? _logger;

        public {className}(IServiceProvider serviceProvider, IBrokerConnection connection)
        {{
            _serviceProvider = serviceProvider;
            _connection = connection;
            _logger = serviceProvider.GetService<ILogger<{className}>>();
        }}

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {{
            _logger?.LogInformation(""Consumer service '{className}' listening on queue '{queueName}'."");

            return _connection.StartConsumingAsync(""{queueName}"", async (routingKey, body, headers) =>
            {{
                using var scope = _serviceProvider.CreateScope();
                using var consumeScope = global::LinKit.Core.Messaging.MessageConsumeInterceptors.Enter(scope.ServiceProvider, headers);
                var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

                // Extract message type from linkit headers or fallback to routingKey
                var messageTypeHeader = headers.TryGetValue(MessageHeaderNames.MessageType, out var typeVal) ? typeVal?.ToString() : null;
                if (string.IsNullOrEmpty(messageTypeHeader) && headers.TryGetValue(""message_type"", out var legacyTypeVal))
                {{
                    messageTypeHeader = legacyTypeVal?.ToString();
                }}
"
            );

            foreach (var msg in queueGroup)
            {
                var varName = msg.SimpleTypeName.ToLowerInvariant();
                string dispatchCall = msg.IsNotification
                    ? $"await mediator.PublishAsync({varName}, ct: stoppingToken);"
                    : $"await mediator.SendAsync({varName}, stoppingToken);";

                sb.AppendLine(
                    $@"
                if (string.Equals(routingKey, ""{msg.RoutingKey}"", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(messageTypeHeader, ""{msg.SimpleTypeName}"", StringComparison.OrdinalIgnoreCase))
                {{
                    var {varName} = global::LinKit.Core.Messaging.LinKitMessageJson.Deserialize<{msg.MessageType}>(body.Span);
                    if ({varName} is not null)
                    {{"
                );

                if (msg.MaxRetries > 0)
                {
                    sb.AppendLine(
                        $@"
                        int retryCount = 0;
                        while (true)
                        {{
                            try
                            {{
                                {dispatchCall}
                                break;
                            }}
                            catch (Exception ex) when (!stoppingToken.IsCancellationRequested && ++retryCount <= {msg.MaxRetries})
                            {{
                                _logger?.LogWarning(ex, ""Error processing {msg.SimpleTypeName}. Retry attempt {{RetryCount}} of {msg.MaxRetries} after {msg.RetryIntervalMs}ms."", retryCount);
                                await Task.Delay({msg.RetryIntervalMs}, stoppingToken);
                            }}
                            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                            {{"
                    );

                    if (!string.IsNullOrEmpty(msg.DeadLetterQueue))
                    {
                        sb.AppendLine(
                            $@"
                                _logger?.LogError(ex, ""Message '{msg.SimpleTypeName}' failed after {msg.MaxRetries} retries. Routing to Dead Letter Queue '{msg.DeadLetterQueue}'."");
                                var dlqHeaders = new MessageHeaders(headers)
                                {{
                                    [""linkit-death-reason""] = ex.Message,
                                    [""linkit-death-time""] = DateTimeOffset.UtcNow.ToString(""O"")
                                }};
                                var producer = scope.ServiceProvider.GetRequiredService<IBrokerProducer>();
                                await producer.ProduceAsync(string.Empty, ""{msg.DeadLetterQueue}"", {varName}, dlqHeaders, stoppingToken);
                                break;"
                        );
                    }
                    else
                    {
                        sb.AppendLine(
                            $@"
                                _logger?.LogError(ex, ""Message '{msg.SimpleTypeName}' failed after maximum retries ({msg.MaxRetries}). No DLQ configured."");
                                throw;"
                        );
                    }

                    sb.AppendLine(
                        @"                            }
                        }"
                    );
                }
                else
                {
                    sb.AppendLine(
                        $@"
                        {dispatchCall}"
                    );
                }

                sb.AppendLine(
                    @"                        return;
                    }
                }"
                );
            }

            sb.AppendLine(
                $@"
                _logger?.LogDebug(""No consumer handler matched for routing key '{{RoutingKey}}' or type '{{MessageType}}' on queue '{queueName}'."", routingKey, messageTypeHeader);
            }}, stoppingToken);
        }}
    }}"
            );
        }

        sb.AppendLine(@"}");
        return sb.ToString();
    }
}
