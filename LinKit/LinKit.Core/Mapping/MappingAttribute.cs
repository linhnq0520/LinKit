namespace LinKit.Core.Mapping;

[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class MapperContextAttribute : Attribute { }

public interface IMappingConfigurator
{
    void Configure(IMapperConfigurationBuilder builder);
}

public interface IMapperConfigurationBuilder
{
    IMappingExpression<TSource, TDestination> CreateMap<TSource, TDestination>();
}

public interface IMappingExpression<TSource, TDestination>
{
    IMappingExpression<TSource, TDestination> ForMember(
        string destinationMember,
        string sourceMember
    );

    IMappingExpression<TSource, TDestination> ForMember(
        string destinationMember,
        Type converterType,
        string converterMethodName,
        string? sourceMember = null
    );
}
