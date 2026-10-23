using System;

namespace LinKit.Data.Migrations;

/// <summary>
/// Metadata describing a compiled data migration.
/// </summary>
public sealed class MigrationDescriptor
{
    public string Version { get; }
    public string Description { get; }
    public Type MigrationType { get; }
    public Func<IServiceProvider, IMigration> Factory { get; }

    public MigrationDescriptor(
        string version,
        string description,
        Type migrationType,
        Func<IServiceProvider, IMigration> factory)
    {
        Version = version ?? throw new ArgumentNullException(nameof(version));
        Description = description ?? string.Empty;
        MigrationType = migrationType ?? throw new ArgumentNullException(nameof(migrationType));
        Factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }
}

/// <summary>
/// Source-generated registry holding all compiled migrations ordered by version.
/// Eliminates runtime assembly scanning and enables 100% Native AOT compliance.
/// </summary>
public interface IMigrationRegistry
{
    IReadOnlyList<MigrationDescriptor> GetMigrations();
}
