using System.Threading;
using System.Threading.Tasks;

namespace LinKit.Data.Initialization;

/// <summary>
/// Initializes and ensures the target database and users/privileges exist before schema synchronization.
/// </summary>
public interface IDatabaseInitializer
{
    /// <summary>Creates the database and its application user when the options ask for it.</summary>
    /// <param name="ct">Token used to cancel the initialization.</param>
    /// <returns>A task that completes when the database is ready for schema synchronization.</returns>
    Task InitializeAsync(CancellationToken ct = default);
}
