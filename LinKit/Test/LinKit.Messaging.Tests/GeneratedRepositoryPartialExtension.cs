namespace LinKit.Messaging.Tests;

public partial class KeyIntIdentityRepository
{
    public static string CustomMarker => "extended";

    public Task<int> CustomOperationAsync() => Task.FromResult(42);
}
