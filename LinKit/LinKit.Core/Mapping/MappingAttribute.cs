namespace LinKit.Core.Mapping
{
    /// <summary>Đánh dấu class cấu hình mapping (phải partial)</summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    public sealed class MapperContextAttribute : Attribute { }

    public interface IMappingConfigurator
    {
        void Configure(IMapperConfigurationBuilder builder);
    }

    /// <summary>Builder chỉ là “API ảo” để generator phân tích syntax.</summary>
    public interface IMapperConfigurationBuilder
    {
        IMappingExpression<TSource, TDestination> CreateMap<TSource, TDestination>();
    }

    /// <summary>Chaining .ForMember(...) để mô tả rule cho generator.</summary>
    public interface IMappingExpression<TSource, TDestination>
    {
        // Rule #2: map khác tên
        IMappingExpression<TSource, TDestination> ForMember(string destinationMember, string sourceMember);

        // Rule #3: map bằng converter
        // Nếu sourceMember null ⇒ coi converter nhận cả object source (ít dùng), còn lại là nhận source.property
        IMappingExpression<TSource, TDestination> ForMember(
            string destinationMember,
            Type converterType,
            string converterMethodName,
            string? sourceMember = null
        );
    }
}
