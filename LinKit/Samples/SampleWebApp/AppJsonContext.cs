using System.Text.Json.Serialization;
using Contract.Models;
using Grpc.Core;
using SampleWebApp.Features.Hello;
using SampleWebApp.Features.Users;

namespace SampleWebApp;

[JsonSerializable(typeof(UserDto))]
[JsonSerializable(typeof(GetUserQuery))]
[JsonSerializable(typeof(CreateUserCommand))]
[JsonSerializable(typeof(Metadata))]
[JsonSerializable(typeof(HelloReply))]
[JsonSerializable(typeof(HelloQuery))]
public partial class AppJsonContext : JsonSerializerContext { }
