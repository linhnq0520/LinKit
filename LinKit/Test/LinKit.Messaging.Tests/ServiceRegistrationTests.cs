using System;
using System.Collections.Generic;
using System.Reflection;
using LinKit.Caching;
using LinKit.Grpc;
using LinKit.Messaging.Kafka;
using LinKit.Messaging.RabbitMQ;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace LinKit.Messaging.Tests;

/// <summary>
/// Guards the registration contract: composition roots expose one documented entry point, and a misconfigured
/// broker or cache fails at startup instead of degrading on the first message.
/// </summary>
public sealed class ServiceRegistrationTests
{
    [Fact]
    public void AddLinKitGrpc_RegistersTheCompleteDefaultGraph()
    {
        using var provider = new ServiceCollection()
            .AddLinKitGrpc("https://localhost:7001")
            .BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IGrpcChannelProvider>());
        Assert.NotNull(provider.GetRequiredService<IGrpcClientFactory>());
        Assert.NotNull(provider.GetRequiredService<IGrpcInterceptorProvider>());
        Assert.NotNull(provider.GetRequiredService<IMetadataProvider>());
    }

    [Fact]
    public void AddLinKitGrpc_KeepsACustomProviderRegisteredLater()
    {
        var services = new ServiceCollection();
        services.AddLinKitGrpc("https://localhost:7001");
        // Registration order decides the winner, so an application override that runs afterwards takes effect.
        services.AddSingleton<IGrpcChannelProvider, CustomChannelProvider>();

        using var provider = services.BuildServiceProvider();

        Assert.IsType<CustomChannelProvider>(provider.GetRequiredService<IGrpcChannelProvider>());
    }

    [Fact]
    public void AddLinKitGrpc_RejectsMissingAddress() =>
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddLinKitGrpc("  "));

    [Fact]
    public void KafkaOptions_FailValidationForABlankGroupId()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Kafka:BootstrapServers"] = "localhost:9092",
                    ["Kafka:GroupId"] = "",
                }
            )
            .Build();

        using var provider = new ServiceCollection()
            .AddLinKitKafka(configuration)
            .BuildServiceProvider();

        var error = Assert.Throws<OptionsValidationException>(() =>
            _ = provider.GetRequiredService<IOptions<KafkaOptions>>().Value
        );
        Assert.Contains("GroupId", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void KafkaOptions_AcceptTheDefaults()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        using var provider = new ServiceCollection()
            .AddLinKitKafka(configuration)
            .BuildServiceProvider();

        Assert.Equal(
            "localhost:9092",
            provider.GetRequiredService<IOptions<KafkaOptions>>().Value.BootstrapServers
        );
    }

    [Fact]
    public void RabbitMqOptions_FailValidationForAnUnknownExchangeType()
    {
        using var provider = new ServiceCollection()
            .AddLinKitRabbitMQ(options => options.DefaultExchangeType = "smoke")
            .BuildServiceProvider();

        var error = Assert.Throws<OptionsValidationException>(() =>
            _ = provider.GetRequiredService<IOptions<RabbitMqOptions>>().Value
        );
        Assert.Contains("DefaultExchangeType", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RabbitMqOptions_FailValidationForAnOutOfRangePort()
    {
        using var provider = new ServiceCollection()
            .AddLinKitRabbitMQ(options => options.Port = 0)
            .BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() =>
            _ = provider.GetRequiredService<IOptions<RabbitMqOptions>>().Value
        );
    }

    [Fact]
    public void RedisOptions_AreValidatedBeforeTheProviderIsRegistered()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddLinKitCaching(options => options.Port = 0)
        );
        Assert.Contains("Port", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RedisOptions_AcceptTheDefaults()
    {
        using var provider = new ServiceCollection()
            .AddLinKitCaching(options => options.Host = "cache.internal")
            .BuildServiceProvider();

        Assert.IsType<HybridRedisCacheProvider>(provider.GetRequiredService<ICacheProvider>());
    }

    [Theory]
    [InlineData("LinKit.Core.Caching.CacheAsideAttribute")]
    [InlineData("LinKit.Core.Caching.InvalidateCacheAttribute")]
    [InlineData("LinKit.Core.Resilience.RetryAttribute")]
    [InlineData("LinKit.Core.Resilience.CircuitBreakerAttribute")]
    [InlineData("LinKit.Core.Resilience.TimeoutAttribute")]
    public void Core_DoesNotAdvertiseMetadataOnlyAttributes(string typeName)
    {
        // These attributes were removed in LinKit.Core 4.0.0: they promised behavior that never existed. The
        // explicit ICacheProvider / ResiliencePipeline APIs are the supported way to get that behavior.
        Assembly core = typeof(LinKit.Core.Messaging.IBrokerProducer).Assembly;

        Assert.Null(core.GetType(typeName, throwOnError: false));
    }

    [Fact]
    public void Core_KeepsTheExplicitResilienceTypesTheAttributesUsedToReference()
    {
        Assembly core = typeof(LinKit.Core.Messaging.IBrokerProducer).Assembly;

        Assert.NotNull(core.GetType("LinKit.Core.Resilience.BackoffType", throwOnError: false));
        Assert.NotNull(
            core.GetType("LinKit.Core.Resilience.CircuitBreakerOpenException", throwOnError: false)
        );
    }

    private sealed class CustomChannelProvider : IGrpcChannelProvider
    {
        // Fully qualified through global:: because the LinKit.Grpc namespace shadows the Grpc.* namespaces here.
        public global::Grpc.Net.Client.GrpcChannel GetChannelFor<TClient>()
            where TClient : global::Grpc.Core.ClientBase =>
            global::Grpc.Net.Client.GrpcChannel.ForAddress("https://custom.example.net");
    }
}
