using System.Collections.Generic;
using System.Linq;
using System.Text;
using LinKit.Core.Abstractions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace LinKit.Generator.Generators;

[Generator]
public class RootGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var cqrsServices = CqrsGeneratorPart.GetServices(context);
        //var grpcClientServices = GrpcClientGeneratorPart.GetServices(context);
        var diServices = DependencyInjectionGeneratorPart.GetServices(context);
        //var grpcServices = GrpcGeneratorPart.GetServices(context);
        var messagingServices = MessagingGeneratorPart.GetServices(context);

        CqrsGeneratorPart.Initialize(context);
        //GrpcClientGeneratorPart.Initialize(context);
        //GrpcGeneratorPart.Initialize(context);
        EndpointsGeneratorPart.Initialize(context);
        MessagingGeneratorPart.Initialize(context);
        MapperGeneratorPart.Initialize(context);

        var allServices = cqrsServices
            .Combine(diServices)
            .Combine(messagingServices)
            .Select(
                (combined, _) =>
                    new AllServicesInfo
                    {
                        CqrsServices = combined.Left.Left,
                        DIServices = combined.Left.Right,
                        MessagingServices = combined.Right,
                    }
            );

        context.RegisterSourceOutput(
            allServices,
            (spc, services) =>
            {
                // --- CQRS ---
                if (services.CqrsServices.Any())
                {
                    var src = GeneratePartialDI(
                        services.CqrsServices.Select(s => s.RegistrationCode),
                        "LinKit.Core",
                        "AddLinKitCqrs",
                        "CQRS Services (Mediator, Handlers, Behaviors)"
                    );
                    spc.AddSource($"Cqrs.DependencyInjection.g.cs", SourceText.From(src, Encoding.UTF8));
                }

                //// --- gRPC Client ---
                //if (services.GrpcClientServices.Any())
                //{
                //    var src = GeneratePartialDI(
                //        services.GrpcClientServices.Select(s => s.RegistrationCode),
                //        "LinKit.Core",
                //        "AddLinKitGrpcClient",
                //        "gRPC Client Mediator"
                //    );
                //    spc.AddSource($"GrpcClient.DependencyInjection.g.cs", SourceText.From(src, Encoding.UTF8));
                //}

                //// --- gRPC Server ---
                //if (services.GrpcServices.Any())
                //{
                //    var src = GeneratePartialDI(
                //        services.GrpcServices.Select(s => s.RegistrationCode),
                //        "LinKit.Core",
                //        "AddLinKitGrpcServer",
                //        "gRPC Server Services (Generated Implementations)"
                //    );
                //    spc.AddSource($"GrpcServer.DependencyInjection.g.cs", SourceText.From(src, Encoding.UTF8));
                //}

                // --- Custom DI Services ---
                if (services.DIServices.Any())
                {
                    var src = GeneratePartialDI(
                        services.DIServices.Select(s =>
                        {
                            // Remove global:: prefix for cleaner type names
                            var serviceType = s.ServiceType.StartsWith("global::") ? s.ServiceType.Substring(8) : s.ServiceType;
                            var implType = s.ImplementationType.StartsWith("global::") ? s.ImplementationType.Substring(8) : s.ImplementationType;

                            var lifetime = ((Lifetime)s.Lifetime) switch
                            {
                                Lifetime.Scoped => "AddScoped",
                                Lifetime.Singleton => "AddSingleton",
                                _ => "AddTransient",
                            };

                            if (s.IsGeneric && !string.IsNullOrWhiteSpace(s.Key))
                            {
                                // Handle keyed generic registration (e.g., services.AddKeyedScoped(typeof(IRepository<>), "key", typeof(BaseRepository<>)))
                                lifetime = ((Lifetime)s.Lifetime) switch
                                {
                                    Lifetime.Scoped => "AddKeyedScoped",
                                    Lifetime.Singleton => "AddKeyedSingleton",
                                    _ => "AddKeyedTransient",
                                };
                                return $"services.{lifetime}(typeof({serviceType}), \"{s.Key}\", typeof({implType}));";
                            }
                            else if (s.IsGeneric)
                            {
                                // Handle generic registration (e.g., services.AddScoped(typeof(IABC<>), typeof(CreateUserValidator<>)))
                                return $"services.{lifetime}(typeof({serviceType}), typeof({implType}));";
                            }
                            else if (!string.IsNullOrWhiteSpace(s.Key))
                            {
                                // Handle keyed non-generic registration (e.g., services.AddKeyedScoped<IValidator, Validator>("key"))
                                lifetime = ((Lifetime)s.Lifetime) switch
                                {
                                    Lifetime.Scoped => "AddKeyedScoped",
                                    Lifetime.Singleton => "AddKeyedSingleton",
                                    _ => "AddKeyedTransient",
                                };
                                return $"services.{lifetime}<{serviceType}, {implType}>(\"{s.Key}\");";
                            }
                            else
                            {
                                // Handle standard non-generic registration (e.g., services.AddScoped<IValidator, Validator>())
                                return $"services.{lifetime}<{serviceType}, {implType}>();";
                            }
                        }),
                        "LinKit.Core",
                        "AddLinKitDependency",
                        "Custom Registered Services via [RegisterService]"
                    );
                    spc.AddSource($"CustomDI.DependencyInjection.g.cs", SourceText.From(src, Encoding.UTF8));
                }

                // --- Messaging ---
                if (services.MessagingServices.Any())
                {
                    var src = GeneratePartialDI(
                        services.MessagingServices.Select(s => s.RegistrationCode),
                        "LinKit.Core",
                        "AddLinKitMessaging",
                        "Messaging Services (Publisher, Consumers)"
                    );
                    spc.AddSource($"Messaging.DependencyInjection.g.cs", SourceText.From(src, Encoding.UTF8));
                }
            }
        );
    }

    private static string GeneratePartialDI(IEnumerable<string> registrations, string @namespace, string methodName, string comment)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            @"// <auto-generated/> by LinKit.Generator
#nullable enable
using Microsoft.Extensions.DependencyInjection;
using LinKit.Core.Abstractions;"
        );

        sb.AppendLine($"namespace {@namespace}");
        sb.AppendLine(@"{
    public static partial class ServicesExtensions
    {");
        sb.AppendLine($"        public static IServiceCollection {methodName}(this IServiceCollection services)");
        sb.AppendLine("        {");
        sb.AppendLine($"            // --- {comment} ---");

        foreach (var reg in registrations.Distinct())
        {
            sb.AppendLine($"            {reg}");
        }

        sb.AppendLine(
    @"            return services;
        }
    }
}"
        );
        return sb.ToString();
    }
}

internal record AllServicesInfo
{
    public IReadOnlyList<CqrsServiceInfo> CqrsServices { get; init; } = new List<CqrsServiceInfo>();
    //public IReadOnlyList<GrpcClientServiceInfo> GrpcClientServices { get; init; } = new List<GrpcClientServiceInfo>();
    public IReadOnlyList<ServiceInfo> DIServices { get; init; } = new List<ServiceInfo>();
    //public IReadOnlyList<GrpcServiceInfo> GrpcServices { get; init; } = new List<GrpcServiceInfo>();
    public IReadOnlyList<MessagingServiceInfo> MessagingServices { get; init; } = new List<MessagingServiceInfo>();
}

internal record CqrsServiceInfo(string RegistrationCode);
//internal record GrpcClientServiceInfo(string RegistrationCode);
//internal record GrpcServiceInfo(string RegistrationCode);
internal record MessagingServiceInfo(string RegistrationCode);