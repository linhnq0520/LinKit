using System;
using System.Linq.Expressions;

namespace LinKit.Core.Mapping;

/// <summary>
/// Marks the class that declares mapping profiles for a module. The source generator collects every
/// <see cref="IMappingConfigurator"/> in the same assembly and emits compile-time mapping code, so no reflection is used
/// at runtime.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class MapperContextAttribute : Attribute { }

/// <summary>
/// Marks a class that calls <see cref="IMapperConfigurationBuilder.CreateMap{TSource, TDestination}"/> in
/// <see cref="IMappingConfigurator.Configure"/> to describe how one type maps to another.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class MappingProfileAttribute : Attribute { }

/// <summary>
/// Implemented by a mapping profile to declare the maps of a module.
/// </summary>
public interface IMappingConfigurator
{
    /// <summary>
    /// Declares the maps of the profile.
    /// </summary>
    /// <param name="builder">Builder used to create mapping expressions.</param>
    void Configure(IMapperConfigurationBuilder builder);
}

/// <summary>
/// Entry point used by a mapping profile to declare maps.
/// </summary>
public interface IMapperConfigurationBuilder
{
    /// <summary>
    /// Declares a map from <typeparamref name="TSource"/> to <typeparamref name="TDestination"/> and returns the
    /// expression used to customize individual members.
    /// </summary>
    /// <typeparam name="TSource">Source type.</typeparam>
    /// <typeparam name="TDestination">Destination type.</typeparam>
    /// <returns>A fluent expression for member-level configuration.</returns>
    IMappingExpression<TSource, TDestination> CreateMap<TSource, TDestination>();
}

/// <summary>
/// Member-level configuration for a single destination member.
/// </summary>
/// <typeparam name="TSource">Source type of the map.</typeparam>
/// <typeparam name="TDestination">Destination type of the map.</typeparam>
public interface IMappingOptions<TSource, TDestination>
{
    /// <summary>
    /// Excludes the destination member from mapping, leaving it at its default value.
    /// </summary>
    void Ignore();

    /// <summary>
    /// Maps the destination member from <paramref name="sourceExpression"/> instead of the member with the same name.
    /// </summary>
    /// <typeparam name="TSourceMember">Type of the selected source member.</typeparam>
    /// <param name="sourceExpression">Expression selecting the source value.</param>
    void MapFrom<TSourceMember>(Expression<Func<TSource, TSourceMember>> sourceExpression);

    /// <summary>
    /// Maps the destination member through <paramref name="converterMethodName"/>, a static method of
    /// <typeparamref name="TConverter"/>.
    /// </summary>
    /// <typeparam name="TConverter">Type that declares the conversion method.</typeparam>
    /// <typeparam name="TSourceMember">Type of the selected source member.</typeparam>
    /// <param name="converterMethodName">Name of the static method that converts the source value.</param>
    /// <param name="sourceExpression">Expression selecting the source value.</param>
    void ConvertWith<TConverter, TSourceMember>(
        string converterMethodName,
        Expression<Func<TSource, TSourceMember>> sourceExpression
    );

    /// <summary>
    /// Maps the destination member through a conversion method declared on <paramref name="converterType"/>, allowing the
    /// converter to be selected at runtime.
    /// </summary>
    /// <typeparam name="TSourceMember">Type of the selected source member.</typeparam>
    /// <param name="converterType">Type that declares the conversion method.</param>
    /// <param name="converterMethodName">Name of the static method that converts the source value.</param>
    /// <param name="sourceExpression">Expression selecting the source value.</param>
    void ConvertWith<TSourceMember>(
        Type converterType,
        string converterMethodName,
        Expression<Func<TSource, TSourceMember>> sourceExpression
    );
}

/// <summary>
/// Fluent expression returned by <see cref="IMapperConfigurationBuilder.CreateMap{TSource, TDestination}"/>.
/// </summary>
/// <typeparam name="TSource">Source type of the map.</typeparam>
/// <typeparam name="TDestination">Destination type of the map.</typeparam>
public interface IMappingExpression<TSource, TDestination>
{
    /// <summary>
    /// Configures one destination member.
    /// </summary>
    /// <typeparam name="TMember">Type of the destination member.</typeparam>
    /// <param name="destinationMember">Expression selecting the destination member.</param>
    /// <param name="memberOptions">Callback that configures the member.</param>
    /// <returns>The same expression, so calls can be chained.</returns>
    IMappingExpression<TSource, TDestination> ForMember<TMember>(
        Expression<Func<TDestination, TMember>> destinationMember,
        Action<IMappingOptions<TSource, TDestination>> memberOptions
    );
}
