using Microsoft.CodeAnalysis;

namespace LinKit.Generator.Generators;

[Generator]
public class RootGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        CqrsGeneratorPart.Initialize(context);
        EndpointsGeneratorPart.Initialize(context);
        GrpcGeneratorPart.Initialize(context);
        DependencyInjectionGeneratorPart.Initialize(context);
    }
}
