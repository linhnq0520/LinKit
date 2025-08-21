using SampleWebApp.Features.Users;
using System.Text.Json.Serialization;

namespace SampleWebApp;

[JsonSerializable(typeof(UserDto))]
[JsonSerializable(typeof(GetUserQuery))]
[JsonSerializable(typeof(CreateUserCommand))]
public partial class AppJsonContext : JsonSerializerContext
{
}
