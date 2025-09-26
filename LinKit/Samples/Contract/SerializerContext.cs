using System.Text.Json.Serialization;
using Contract.Models;

namespace Contract;

[JsonSerializable(typeof(ExtraInfo))]
internal partial class SerializerContext : JsonSerializerContext { }
