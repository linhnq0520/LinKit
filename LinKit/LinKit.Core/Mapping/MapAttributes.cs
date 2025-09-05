//namespace LinKit.Core.Mapping;

///// <summary>
///// Marks a partial class to generate mapping extension methods from a source type.
///// The class must be declared as partial.
///// </summary>
//[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = true)]
//public sealed class MapFromAttribute : Attribute
//{
//    public Type SourceType { get; }
//    public MapFromAttribute(Type sourceType)
//    {
//        SourceType = sourceType;
//    }
//}

///// <summary>
///// Marks a partial class to generate mapping extension methods to a destination type.
///// The class must be declared as partial.
///// </summary>
//[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = true)]
//public sealed class MapToAttribute : Attribute
//{
//    public Type DestinationType { get; }
//    public MapToAttribute(Type destinationType)
//    {
//        DestinationType = destinationType;
//    }
//}

///// <summary>
///// Explicitly configures how a property on the destination object is mapped.
///// </summary>
//[AttributeUsage(AttributeTargets.Property, Inherited = false, AllowMultiple = false)]
//public sealed class MapPropertyAttribute : Attribute
//{
//    /// <summary>
//    /// Specifies the name of the property on the source object to map from.
//    /// Use this when property names do not match.
//    /// </summary>
//    public string? SourcePropertyName { get; }

//    /// <summary>
//    /// Specifies the type containing the custom converter method.
//    /// The method must be static.
//    /// </summary>
//    public Type? ConverterType { get; set; }

//    /// <summary>
//    /// Specifies the name of the static method on the ConverterType to use for mapping.
//    /// The method must accept the source property's type and return the destination property's type.
//    /// </summary>
//    public string? ConverterMethodName { get; set; }

//    /// <summary>
//    /// Maps a property by its name.
//    /// </summary>
//    /// <param name="sourcePropertyName">The name of the property on the source object.</param>
//    public MapPropertyAttribute(string sourcePropertyName)
//    {
//        SourcePropertyName = sourcePropertyName;
//    }

//    /// <summary>
//    /// Maps a property using custom conversion logic, typically inferring the source property by name.
//    /// </summary>
//    public MapPropertyAttribute()
//    {
//        SourcePropertyName = null;
//    }
//}