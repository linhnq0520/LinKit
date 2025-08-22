using System;

namespace LinKit.Core.Endpoints;

[AttributeUsage(AttributeTargets.Property)]
public sealed class FromRouteAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Property)]
public sealed class FromQueryAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Property)]
public sealed class FromHeaderAttribute : Attribute { }
