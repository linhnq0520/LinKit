using Contract.Models;
using System.Text.Json.Serialization;

namespace Contract
{
    [JsonSerializable(typeof(ExtraInfo))]
    internal partial class SerializerContext : JsonSerializerContext
    {
    }
}
