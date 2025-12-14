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
        IncrementalValueProvider<IReadOnlyList<CqrsServiceInfo>> cqrsServices =
            CqrsGeneratorPart.GetServices(context);
        IncrementalValueProvider<IReadOnlyList<ServiceInfo>> diServices =
            DependencyInjectionGeneratorPart.GetServices(context);
        IncrementalValueProvider<IReadOnlyList<MessagingServiceInfo>> messagingServices =
            MessagingGeneratorPart.GetServices(context);

        CqrsGeneratorPart.Initialize(context);
        EndpointsGeneratorPart.Initialize(context);
        MessagingGeneratorPart.Initialize(context);
        MapperGeneratorPart.Initialize(context);
        BackgroundJobGeneratorPart.Initialize(context);

        IncrementalValueProvider<AllServicesInfo> allServices = cqrsServices
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
                bool hasGeneratedAnyFile = false;

                // --- CQRS ---
                if (services.CqrsServices.Any())
                {
                    hasGeneratedAnyFile = true;
                    string src = GenerateCqrsDI(
                        services.CqrsServices.Select(s => s.RegistrationCode),
                        "LinKit.Core",
                        "AddLinKitCqrs",
                        "CQRS Services (Mediator, Handlers, Behaviors)"
                    );
                    spc.AddSource(
                        $"Cqrs.DependencyInjection.g.cs",
                        SourceText.From(src, Encoding.UTF8)
                    );
                }

                // --- Custom DI Services ---
                if (services.DIServices.Any())
                {
                    hasGeneratedAnyFile = true;
                    string src = GeneratePartialDI(
                        services.DIServices.Select(s =>
                        {
                            // Remove global:: prefix for cleaner type names
                            string serviceType = s.ServiceType.StartsWith("global::")
                                ? s.ServiceType.Substring(8)
                                : s.ServiceType;
                            string implType = s.ImplementationType.StartsWith("global::")
                                ? s.ImplementationType.Substring(8)
                                : s.ImplementationType;

                            string lifetime = ((Lifetime)s.Lifetime) switch
                            {
                                Lifetime.Scoped => "AddScoped",
                                Lifetime.Singleton => "AddSingleton",
                                _ => "AddTransient",
                            };

                            if (s.IsGeneric && !string.IsNullOrWhiteSpace(s.Key))
                            {
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
                                return $"services.{lifetime}(typeof({serviceType}), typeof({implType}));";
                            }
                            else if (!string.IsNullOrWhiteSpace(s.Key))
                            {
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
                                return $"services.{lifetime}<{serviceType}, {implType}>();";
                            }
                        }),
                        "LinKit.Core",
                        "AddLinKitDependency",
                        "Custom Registered Services via [RegisterService]"
                    );
                    spc.AddSource(
                        $"CustomDI.DependencyInjection.g.cs",
                        SourceText.From(src, Encoding.UTF8)
                    );
                }

                // --- Messaging ---
                if (services.MessagingServices.Any())
                {
                    hasGeneratedAnyFile = true;
                    string src = GeneratePartialDI(
                        services.MessagingServices.Select(s => s.RegistrationCode),
                        "LinKit.Core",
                        "AddLinKitMessaging",
                        "Messaging Services (Publisher, Consumers)"
                    );
                    spc.AddSource(
                        $"Messaging.DependencyInjection.g.cs",
                        SourceText.From(src, Encoding.UTF8)
                    );
                }

                if (hasGeneratedAnyFile)
                {
                    HashSet<string> usings = new HashSet<string> { "LinKit.Core" };

                    StringBuilder globalUsingsSource = new StringBuilder();
                    globalUsingsSource.AppendLine("// <auto-generated/> by LinKit.Generator");
                    globalUsingsSource.AppendLine();
                    foreach (string u in usings)
                    {
                        globalUsingsSource.AppendLine($"global using {u};");
                    }

                    spc.AddSource(
                        "GlobalUsings.g.cs",
                        SourceText.From(globalUsingsSource.ToString(), Encoding.UTF8)
                    );
                }
            }
        );
    }

    private static string GeneratePartialDI(
        IEnumerable<string> registrations,
        string @namespace,
        string methodName,
        string comment
    )
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine(
            @"// <auto-generated/> by LinKit.Generator
#nullable enable
using Microsoft.Extensions.DependencyInjection;
using LinKit.Core.Abstractions;"
        );

        sb.AppendLine($"namespace {@namespace}");
        sb.AppendLine(
            @"{
    internal static partial class ServicesExtensions
    {"
        );
        sb.AppendLine(
            $"        public static IServiceCollection {methodName}(this IServiceCollection services)"
        );
        sb.AppendLine("        {");
        sb.AppendLine($"            // --- {comment} ---");

        foreach (string? reg in registrations.Distinct())
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

    private static string GenerateCqrsDI(
        IEnumerable<string> registrations,
        string @namespace,
        string methodName,
        string comment
    )
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine(
            @"// <auto-generated/> by LinKit.Generator
#nullable enable
using Microsoft.Extensions.DependencyInjection;
using LinKit.Core.Abstractions;"
        );

        sb.AppendLine($"namespace {@namespace}");
        sb.AppendLine(
            @"{
    internal static partial class ServicesExtensions
    {"
        );
        sb.AppendLine(
            $"        public static IServiceCollection {methodName}(this IServiceCollection services, string? mediatorKey = null)"
        );
        sb.AppendLine("        {");
        sb.AppendLine($"            // --- {comment} ---");
        sb.AppendLine($"            if(!string.IsNullOrWhiteSpace(mediatorKey))");
        sb.AppendLine("            {");
        sb.AppendLine(
            $"                 services.AddKeyedScoped<LinKit.Core.Cqrs.IMediator, LinKit.Generated.Cqrs.Mediator>(mediatorKey);"
        );
        sb.AppendLine("            }");
        sb.AppendLine("            else");
        sb.AppendLine("            {");
        sb.AppendLine(
            $"                 services.AddScoped<LinKit.Core.Cqrs.IMediator, LinKit.Generated.Cqrs.Mediator>();"
        );
        sb.AppendLine("            }");

        foreach (string? reg in registrations.Distinct())
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

// Các record class giữ nguyên không đổi
internal record AllServicesInfo
{
    public IReadOnlyList<CqrsServiceInfo> CqrsServices { get; init; } = [];
    public IReadOnlyList<ServiceInfo> DIServices { get; init; } = [];
    public IReadOnlyList<MessagingServiceInfo> MessagingServices { get; init; } = [];
}

internal record CqrsServiceInfo(string RegistrationCode);

internal record MessagingServiceInfo(string RegistrationCode);
