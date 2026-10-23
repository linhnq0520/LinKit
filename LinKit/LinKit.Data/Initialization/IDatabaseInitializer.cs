using System.Threading;
using System.Threading.Tasks;

namespace LinKit.Data.Initialization;

/// <summary>
/// Initializes and ensures the target database and users/privileges exist before schema synchronization.
/// </summary>
public interface IDatabaseInitializer
{
    Task InitializeAsync(CancellationToken ct = default);
}
