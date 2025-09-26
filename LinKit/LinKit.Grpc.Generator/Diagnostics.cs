using Microsoft.CodeAnalysis;

namespace LinKit.Grpc.Generator;

internal static class Diagnostics
{
    public static readonly DiagnosticDescriptor LKG001_MethodNotFound = new(
        id: "LKG001",
        title: "gRPC method not found",
        messageFormat: "The gRPC method '{0}' was not found on the client type '{1}'",
        category: "LinKit.Grpc.Generator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public static readonly DiagnosticDescriptor LKG002_InvalidMethodSignature = new(
        id: "LKG002",
        title: "Invalid gRPC method signature",
        messageFormat: "The gRPC method '{0}' does not have a valid unary signature (e.g., must return AsyncUnaryCall<TResponse>)",
        category: "LinKit.Grpc.Generator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public static readonly DiagnosticDescriptor LKG003_MissingCqrsInterface = new(
        id: "LKG003",
        title: "Missing CQRS interface",
        messageFormat: "The type '{0}' must implement IQuery<TResult>, ICommand<TResult>, or ICommand to use the [GrpcClient] attribute",
        category: "LinKit.Grpc.Generator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public static readonly DiagnosticDescriptor LKG004_InvalidAttributeUsage = new(
        id: "LKG004",
        title: "Invalid GrpcClient attribute usage",
        messageFormat: "The [GrpcClient] attribute requires a gRPC client type and a method name string as arguments",
        category: "LinKit.Grpc.Generator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );
}

internal static class ServerDiagnostics
{
    public static readonly DiagnosticDescriptor LKG101_MethodNotFound = new(
        id: "LKG101",
        title: "gRPC service method not found",
        messageFormat: "The gRPC method '{0}' was not found on the service base type '{1}'",
        category: "LinKit.Grpc.Generator.Server",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public static readonly DiagnosticDescriptor LKG102_InvalidMethodSignature = new(
        id: "LKG102",
        title: "Invalid gRPC service method signature",
        messageFormat: "The gRPC method '{0}' must have a signature like 'Task<TResponse> Method(TRequest request, ServerCallContext context)'",
        category: "LinKit.Grpc.Generator.Server",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public static readonly DiagnosticDescriptor LKG103_MissingCqrsInterface = new(
        id: "LKG103",
        title: "Missing CQRS interface",
        messageFormat: "The type '{0}' must implement IQuery<TResult> or ICommand to use the [GrpcEndpoint] attribute",
        category: "LinKit.Grpc.Generator.Server",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public static readonly DiagnosticDescriptor LKG104_InvalidAttributeUsage = new(
        id: "LKG104",
        title: "Invalid GrpcEndpoint attribute usage",
        messageFormat: "The [GrpcEndpoint] attribute requires a gRPC service base type and a method name string as arguments",
        category: "LinKit.Grpc.Generator.Server",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public static readonly DiagnosticDescriptor LKG105_UnresolvedResponseType = new(
        id: "LKG105",
        title: "Unresolved CQRS response type",
        messageFormat: "Could not resolve the response type for CQRS request '{0}'. Ensure it implements IQuery<T> or ICommand<T> correctly.",
        category: "LinKit.Grpc.Generator.Server",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );
}
