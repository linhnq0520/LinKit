using System;
using System.Linq.Expressions;

namespace LinKit.Core.Mapping
{
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

    public interface IMappingOptions<TSource, TDestination>
    {
        void Ignore();

        void MapFrom<TSourceMember>(Expression<Func<TSource, TSourceMember>> sourceExpression);

        void ConvertWith<TConverter, TSourceMember>(
            string converterMethodName,
            Expression<Func<TSource, TSourceMember>> sourceExpression
        );

        void ConvertWith<TSourceMember>(
            Type converterType,
            string converterMethodName,
            Expression<Func<TSource, TSourceMember>> sourceExpression
        );
    }

    public interface IMappingExpression<TSource, TDestination>
    {
        IMappingExpression<TSource, TDestination> ForMember<TMember>(
            Expression<Func<TDestination, TMember>> destinationMember,
            Action<IMappingOptions<TSource, TDestination>> memberOptions
        );
    }
}
