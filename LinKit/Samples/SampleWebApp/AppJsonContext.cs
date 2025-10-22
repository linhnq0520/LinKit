//using Contract.Models;
using Grpc.Core;
using SampleWebApp.Features.Users;
using System.Text.Json.Serialization;

namespace SampleWebApp;

//[JsonSerializable(typeof(UserDto))]
[JsonSerializable(typeof(GetUserQuery))]
[JsonSerializable(typeof(CreateUserCommand))]
[JsonSerializable(typeof(Metadata))]
public partial class AppJsonContext : JsonSerializerContext { }
