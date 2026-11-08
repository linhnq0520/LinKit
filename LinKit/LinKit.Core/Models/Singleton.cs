namespace LinKit.Core.Models;

/// <summary>
/// Process-wide holder for a single instance of <typeparamref name="T"/>, for cases where constructor injection is not
/// available (static helpers, legacy entry points). Prefer registering the type in the DI container; this type exists as
/// an escape hatch and enforces a set-once contract so the instance cannot be swapped at runtime.
/// </summary>
/// <typeparam name="T">Reference type held by the singleton.</typeparam>
public sealed class Singleton<T>
    where T : class
{
    private static T? _instance;
    private static readonly object _lock = new();

    /// <summary>
    /// Gets the instance previously supplied to <see cref="SetInstance"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">No instance has been set yet.</exception>
    public static T Instance
    {
        get
        {
            if (_instance is null)
            {
                throw new InvalidOperationException(
                    $"{typeof(T).Name} has not been set for the Singleton."
                );
            }

            return _instance;
        }
    }

    /// <summary>
    /// Sets the instance. Safe to call from several threads; only the first call takes effect.
    /// </summary>
    /// <param name="instance">The instance to hold.</param>
    /// <exception cref="ArgumentNullException"><paramref name="instance"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">An instance has already been set.</exception>
    public static void SetInstance(T instance)
    {
        if (instance is null)
        {
            throw new ArgumentNullException(nameof(instance), "Instance cannot be null.");
        }

        lock (_lock)
        {
            if (_instance is not null)
            {
                throw new InvalidOperationException(
                    $"{typeof(T).Name} has already been set. A Singleton can only be set once."
                );
            }

            _instance = instance;
        }
    }
}
