using System.Threading;
using System.Threading.Tasks;

namespace LinKit.Data;

/// <summary>
/// Contract for versioned database migration (schema alteration and/or data seeding/transform).
/// </summary>
public interface IMigration
{
    /// <summary>
    /// Executes migration operations forward (seeding, data transformations, renames, drops).
    /// </summary>
    Task UpAsync(IMigrationContext context, CancellationToken ct = default);

    /// <summary>
    /// Optional rollback operations if migration needs to be reverted.
    /// </summary>
    Task DownAsync(IMigrationContext context, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// Backward-compatible alias for IMigration.
/// </summary>
public interface IDataMigration : IMigration
{
}
