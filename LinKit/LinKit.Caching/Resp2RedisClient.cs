using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LinKit.Caching;

/// <summary>
/// Options for <see cref="HybridRedisCacheProvider"/>.
/// </summary>
public sealed class LinKitRedisOptions
{
    public const string DefaultSection = "Redis";

    public bool Enabled { get; set; } = true;
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 6379;
    public string? Password { get; set; }
    public int Database { get; set; } = 0;
    public TimeSpan DefaultTtl { get; set; } = TimeSpan.FromMinutes(60);
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// Lightweight, zero-reflection, 100% Native AOT compatible RESP2 Redis client with connection pooling.
/// </summary>
public sealed class Resp2RedisClient : IDisposable
{
    private readonly LinKitRedisOptions _options;
    private readonly ConcurrentBag<PooledConnection> _pool = new();
    private const int MaxPoolSize = 32;

    public Resp2RedisClient(LinKitRedisOptions options)
    {
        _options = options;
    }

    public async Task<byte[]?> GetBytesAsync(string key, CancellationToken ct = default)
    {
        PooledConnection? conn = await RentAsync(ct).ConfigureAwait(false);
        if (conn is null)
        {
            return null;
        }

        try
        {
            await WriteCommandAsync(conn.Stream, ct, "GET", key).ConfigureAwait(false);
            return await ReadBulkBytesAsync(conn, ct).ConfigureAwait(false);
        }
        catch
        {
            conn.Dispose();
            return null;
        }
        finally
        {
            Return(conn);
        }
    }

    public async Task<bool> SetBytesAsync(
        string key,
        byte[] value,
        TimeSpan? ttl,
        CancellationToken ct = default
    )
    {
        PooledConnection? conn = await RentAsync(ct).ConfigureAwait(false);
        if (conn is null)
        {
            return false;
        }

        try
        {
            TimeSpan effectiveTtl = ttl ?? _options.DefaultTtl;
            long ms = (long)effectiveTtl.TotalMilliseconds;
            if (ms > 0)
            {
                await WriteCommandWithRawArgAsync(
                        conn.Stream,
                        ct,
                        "SET",
                        Encoding.UTF8.GetBytes(key),
                        value,
                        Encoding.UTF8.GetBytes("PX"),
                        Encoding.UTF8.GetBytes(ms.ToString())
                    )
                    .ConfigureAwait(false);
            }
            else
            {
                await WriteCommandWithRawArgAsync(
                        conn.Stream,
                        ct,
                        "SET",
                        Encoding.UTF8.GetBytes(key),
                        value
                    )
                    .ConfigureAwait(false);
            }

            string? resp = await ReadSimpleOrBulkStringAsync(conn, ct).ConfigureAwait(false);
            return string.Equals(resp, "OK", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            conn.Dispose();
            return false;
        }
        finally
        {
            Return(conn);
        }
    }

    public async Task<bool> DeleteKeyAsync(string key, CancellationToken ct = default)
    {
        PooledConnection? conn = await RentAsync(ct).ConfigureAwait(false);
        if (conn is null)
        {
            return false;
        }

        try
        {
            await WriteCommandAsync(conn.Stream, ct, "DEL", key).ConfigureAwait(false);
            _ = await ReadLineAsync(conn, ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            conn.Dispose();
            return false;
        }
        finally
        {
            Return(conn);
        }
    }

    private async Task<PooledConnection?> RentAsync(CancellationToken ct)
    {
        while (_pool.TryTake(out PooledConnection? existing))
        {
            if (existing.IsConnected)
            {
                return existing;
            }
            existing.Dispose();
        }

        try
        {
            TcpClient tcp = new() { NoDelay = true };
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_options.ConnectTimeout);
            await tcp.ConnectAsync(_options.Host, _options.Port, cts.Token).ConfigureAwait(false);

            PooledConnection conn = new(tcp);

            if (!string.IsNullOrEmpty(_options.Password))
            {
                await WriteCommandAsync(conn.Stream, cts.Token, "AUTH", _options.Password)
                    .ConfigureAwait(false);
                _ = await ReadLineAsync(conn, cts.Token).ConfigureAwait(false);
            }

            if (_options.Database > 0)
            {
                await WriteCommandAsync(
                        conn.Stream,
                        cts.Token,
                        "SELECT",
                        _options.Database.ToString()
                    )
                    .ConfigureAwait(false);
                _ = await ReadLineAsync(conn, cts.Token).ConfigureAwait(false);
            }

            return conn;
        }
        catch
        {
            return null;
        }
    }

    private void Return(PooledConnection conn)
    {
        if (conn.IsConnected && _pool.Count < MaxPoolSize)
        {
            _pool.Add(conn);
        }
        else
        {
            conn.Dispose();
        }
    }

    private static async Task WriteCommandAsync(
        NetworkStream stream,
        CancellationToken ct,
        string command,
        params string[] args
    )
    {
        int totalArgs = 1 + args.Length;
        StringBuilder sb = new(64);
        sb.Append('*').Append(totalArgs).Append("\r\n");

        byte[] cmdBytes = Encoding.UTF8.GetBytes(command);
        sb.Append('$').Append(cmdBytes.Length).Append("\r\n").Append(command).Append("\r\n");

        foreach (string arg in args)
        {
            int byteCount = Encoding.UTF8.GetByteCount(arg);
            sb.Append('$').Append(byteCount).Append("\r\n").Append(arg).Append("\r\n");
        }

        byte[] payload = Encoding.UTF8.GetBytes(sb.ToString());
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task WriteCommandWithRawArgAsync(
        NetworkStream stream,
        CancellationToken ct,
        string command,
        params byte[][] rawArgs
    )
    {
        int totalArgs = 1 + rawArgs.Length;
        using MemoryStream ms = new(256);

        WriteAscii(ms, $"*{totalArgs}\r\n");
        byte[] cmdBytes = Encoding.UTF8.GetBytes(command);
        WriteAscii(ms, $"${cmdBytes.Length}\r\n");
        ms.Write(cmdBytes, 0, cmdBytes.Length);
        WriteAscii(ms, "\r\n");

        foreach (byte[] arg in rawArgs)
        {
            WriteAscii(ms, $"${arg.Length}\r\n");
            ms.Write(arg, 0, arg.Length);
            WriteAscii(ms, "\r\n");
        }

        byte[] buffer = ms.ToArray();
        await stream.WriteAsync(buffer, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static void WriteAscii(MemoryStream ms, string s)
    {
        byte[] b = Encoding.ASCII.GetBytes(s);
        ms.Write(b, 0, b.Length);
    }

    private static async Task<string?> ReadLineAsync(PooledConnection conn, CancellationToken ct)
    {
        MemoryStream lineBuffer = new(64);
        byte[] oneByte = new byte[1];
        while (true)
        {
            int read = await conn.Stream.ReadAsync(oneByte, ct).ConfigureAwait(false);
            if (read == 0)
            {
                conn.MarkDisconnected();
                return null;
            }

            if (oneByte[0] == (byte)'\n')
            {
                byte[] arr = lineBuffer.ToArray();
                int len = arr.Length;
                if (len > 0 && arr[len - 1] == (byte)'\r')
                {
                    len--;
                }
                return Encoding.UTF8.GetString(arr, 0, len);
            }

            lineBuffer.WriteByte(oneByte[0]);
        }
    }

    private static async Task<string?> ReadSimpleOrBulkStringAsync(
        PooledConnection conn,
        CancellationToken ct
    )
    {
        string? header = await ReadLineAsync(conn, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(header))
        {
            return null;
        }

        if (header[0] == '+')
        {
            return header.Substring(1);
        }

        if (header[0] == '$')
        {
            if (!int.TryParse(header.AsSpan(1), out int len) || len < 0)
            {
                return null;
            }
            byte[] data = await ReadExactBytesAsync(conn, len, ct).ConfigureAwait(false);
            _ = await ReadExactBytesAsync(conn, 2, ct).ConfigureAwait(false); // CRLF
            return Encoding.UTF8.GetString(data);
        }

        return null;
    }

    private static async Task<byte[]?> ReadBulkBytesAsync(
        PooledConnection conn,
        CancellationToken ct
    )
    {
        string? header = await ReadLineAsync(conn, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(header) || header[0] != '$')
        {
            return null;
        }

        if (!int.TryParse(header.AsSpan(1), out int len) || len < 0)
        {
            return null;
        }

        byte[] data = await ReadExactBytesAsync(conn, len, ct).ConfigureAwait(false);
        _ = await ReadExactBytesAsync(conn, 2, ct).ConfigureAwait(false); // CRLF
        return data;
    }

    private static async Task<byte[]> ReadExactBytesAsync(
        PooledConnection conn,
        int count,
        CancellationToken ct
    )
    {
        byte[] buffer = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int read = await conn
                .Stream.ReadAsync(buffer.AsMemory(offset, count - offset), ct)
                .ConfigureAwait(false);
            if (read == 0)
            {
                conn.MarkDisconnected();
                throw new EndOfStreamException();
            }
            offset += read;
        }
        return buffer;
    }

    public void Dispose()
    {
        while (_pool.TryTake(out PooledConnection? conn))
        {
            conn.Dispose();
        }
    }

    private sealed class PooledConnection : IDisposable
    {
        private readonly TcpClient _tcp;
        private bool _disconnected;

        public NetworkStream Stream { get; }

        public bool IsConnected => !_disconnected && _tcp.Connected;

        public PooledConnection(TcpClient tcp)
        {
            _tcp = tcp;
            Stream = tcp.GetStream();
        }

        public void MarkDisconnected() => _disconnected = true;

        public void Dispose()
        {
            _disconnected = true;
            try
            {
                Stream.Dispose();
            }
            catch { }
            try
            {
                _tcp.Dispose();
            }
            catch { }
        }
    }
}

