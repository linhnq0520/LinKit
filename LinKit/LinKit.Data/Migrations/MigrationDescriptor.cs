using System;

namespace LinKit.Data.Migrations;

/// <summary>
/// Metadata describing a compiled data migration.
/// </summary>
public sealed class MigrationDescriptor
{
    /// <summary>Version that determines the execution order.</summary>
    public string Version { get; }

    /// <summary>Human readable description recorded in the migration history table.</summary>
    public string Description { get; }

    /// <summary>CLR type of the migration.</summary>
    public Type MigrationType { get; }

    /// <summary>Creates the migration instance from the migration scope's service provider.</summary>
    public Func<IServiceProvider, IMigration> Factory { get; }

    /// <summary>Creates a descriptor.</summary>
    /// <param name="version">Version that determines the execution order.</param>
    /// <param name="description">Human readable description; <see langword="null"/> becomes an empty string.</param>
    /// <param name="migrationType">CLR type of the migration.</param>
    /// <param name="factory">Creates the migration from the migration scope's service provider.</param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    public MigrationDescriptor(
        string version,
        string description,
        Type migrationType,
        Func<IServiceProvider, IMigration> factory
    )
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
    /// <summary>Returns every compiled migration, ordered by version.</summary>
    /// <returns>The ordered migrations.</returns>
    IReadOnlyList<MigrationDescriptor> GetMigrations();
}
