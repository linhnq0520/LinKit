//namespace LinKit.Core.Mapping;

///// <summary>
///// Defines the contract for a class that configures object mappings.
///// </summary>
//public interface IMappingConfigurator
//{
//    void Configure(IMapperConfigurationBuilder builder);
//}

///// <summary>
///// Provides a fluent API to build mapping configurations.
///// </summary>
//public interface IMapperConfigurationBuilder
//{
//    /// <summary>
//    /// Starts defining a new mapping from a source type to a destination type.
//    /// </summary>
//    IMappingExpression<TSource, TDestination> CreateMap<TSource, TDestination>();
//}

//public interface IMappingExpression<TSource, TDestination>
//{
//    // ForMember("DestinationPropertyName", "SourcePropertyName")
//    IMappingExpression<TSource, TDestination> ForMember(string destinationMember, string sourceMember);

//    // ForMember("DestinationPropertyName", typeof(MyConverter), "MyMethodName")
//    IMappingExpression<TSource, TDestination> ForMember(string destinationMember, Type converterType, string converterMethodName);
//}