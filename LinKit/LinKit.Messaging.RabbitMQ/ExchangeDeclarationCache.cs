using System.Collections.Concurrent;

namespace LinKit.Messaging.RabbitMQ;

internal sealed class ExchangeDeclarationCache
{
    private readonly ConcurrentDictionary<(string Name, string Type), byte> _declared = new();

    internal bool IsDeclared(string name, string type) => _declared.ContainsKey((name, type));

    internal void MarkDeclared(string name, string type) => _declared.TryAdd((name, type), 0);
}