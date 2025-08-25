namespace LinKit.Core.Models;

public sealed class Singleton<T>
    where T : class
{
    private static T? _instance;
    private static readonly object _lock = new();

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
