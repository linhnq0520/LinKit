using System.Collections.Concurrent;
using Grpc.Core;
using Grpc.Net.Client;

namespace LinKit.Grpc;

/// <summary>
/// Creates and caches one <see cref="GrpcChannel"/> per client type. The cache is bounded: when more than
/// <see cref="MaxChannels"/> distinct clients are used, the least recently used channel is evicted and disposed so an
/// open-ended client-type set cannot leak connections for the lifetime of the process.
/// </summary>
public class DefaultGrpcChannelProvider : IGrpcChannelProvider, IDisposable
{
    /// <summary>The default channel-count ceiling for one provider instance.</summary>
    public const int DefaultMaxChannels = 32;

    private readonly ConcurrentDictionary<Type, ChannelEntry> _channels = new();
    private readonly string _baseAddress;
    private readonly int _maxChannels;
    private long _useCounter;
    private int _disposed;

    public DefaultGrpcChannelProvider(string baseAddress)
        : this(baseAddress, DefaultMaxChannels) { }

    public DefaultGrpcChannelProvider(string baseAddress, int maxChannels)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseAddress);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxChannels);

        _baseAddress = baseAddress;
        _maxChannels = maxChannels;
    }

    /// <summary>The number of channels currently cached.</summary>
    public int ChannelCount => _channels.Count;

    /// <summary>The channel-count ceiling; beyond it the least recently used channel is evicted.</summary>
    public int MaxChannels => _maxChannels;

    public GrpcChannel GetChannelFor<TClient>()
        where TClient : ClientBase
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var entry = _channels.AddOrUpdate(
            typeof(TClient),
            static (_, address) => new ChannelEntry(GrpcChannel.ForAddress(address)),
            static (_, existing, address) => existing.RecreateIfShutdown(address),
            _baseAddress
        );

        // Strictly increasing stamps keep eviction deterministic even when several entries are created within one tick.
        entry.Touch(Interlocked.Increment(ref _useCounter));
        EvictExcessChannels(entry);
        return entry.Channel;
    }

    private void EvictExcessChannels(ChannelEntry current)
    {
        var excess = _channels.Count - _maxChannels;
        if (excess <= 0)
        {
            return;
        }

        var victims = _channels
            .ToArray()
            .Where(pair => !ReferenceEquals(pair.Value, current))
            .OrderBy(pair => pair.Value.LastUsed)
            .Take(excess)
            .Select(pair => pair.Key)
            .ToArray();

        foreach (var clientType in victims)
        {
            if (_channels.TryRemove(clientType, out var removed))
            {
                removed.Dispose();
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var clientType in _channels.Keys.ToArray())
        {
            if (_channels.TryRemove(clientType, out var removed))
            {
                removed.Dispose();
            }
        }

        GC.SuppressFinalize(this);
    }

    private sealed class ChannelEntry
    {
        private readonly object _gate = new();
        private long _lastUsed;

        public ChannelEntry(GrpcChannel channel) => Channel = channel;

        public GrpcChannel Channel { get; private set; }

        public long LastUsed => Volatile.Read(ref _lastUsed);

        public void Touch(long stamp) => Volatile.Write(ref _lastUsed, stamp);

        /// <summary>
        /// Returns the entry itself while the channel is usable, or replaces a shut-down channel so the caller
        /// never receives a dead channel. The double check keeps concurrent callers from building (and leaking)
        /// two replacement channels.
        /// </summary>
        public ChannelEntry RecreateIfShutdown(string address)
        {
            if (Channel.State != ConnectivityState.Shutdown)
            {
                return this;
            }

            lock (_gate)
            {
                if (Channel.State != ConnectivityState.Shutdown)
                {
                    return this;
                }

                var replacement = GrpcChannel.ForAddress(address);
                var previous = Channel;
                Channel = replacement;
                previous.Dispose();
                return this;
            }
        }

        public void Dispose()
        {
            try
            {
                Channel.Dispose();
            }
            catch (Exception)
            {
                // Disposal races with an in-flight call; a channel that cannot be disposed must not break shutdown.
            }
        }
    }
}
