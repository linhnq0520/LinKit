using System;

namespace LinKit.Data;

/// <summary>
/// Marks a class as an ordered, versioned schema alteration or data seed/transform migration.
/// Migrations are discovered at compile-time by LinKit.Data.Generator and run once in exact version order.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public class MigrationAttribute : Attribute
{
    /// <summary>
    /// Unique migration version or timestamp (e.g., "20261021_01" or "1.0.0").
    /// Migrations are executed in ordinal order of this version string.
    /// </summary>
    public string Version { get; }

    /// <summary>
    /// Human-readable description of the migration (logged to __linkit_migrations history table).
    /// </summary>
    public string Description { get; set; } = string.Empty;

    public MigrationAttribute(string version)
    {
        Version = version ?? throw new ArgumentNullException(nameof(version));
    }
}

/// <summary>
/// Backward-compatible alias for [Migration].
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class DataMigrationAttribute : MigrationAttribute
{
    public DataMigrationAttribute(string version) : base(version) { }
}
