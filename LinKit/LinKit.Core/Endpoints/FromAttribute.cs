using System;

namespace LinKit.Core.Endpoints;

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class FromRouteAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class FromQueryAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class FromHeaderAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class FromBodyAttribute : Attribute { }
