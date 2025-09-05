using LinKit.Core.Cqrs;
using LinKit.Core.Grpc;
using SampleWebApp.Grpc.Users;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Contract.Models;

public record UserDto(int Id, string Name);

public class UsersDto
{
    public List<UserDto> Users { get; set; }
}

[GrpcClient(typeof(UserGrpcService.UserGrpcServiceClient), "GetUserAsync")]
public partial class GetUserById : IQuery<UserDto>
{
    public int Id { get; set; }
}

[GrpcClient(typeof(UserGrpcService.UserGrpcServiceClient), "UpdateUserAsync")]
//[MapTo(typeof(UserModel))]
public partial class UpdateUser : ICommand
{
    public int Id { get; set; }

    [JsonPropertyName("user_name")]
    public string UserName { get; set; }
    public string Name { get; set; }
    public ExtraInfo ExtraInfo { get; set; }
}

public class ExtraInfo
{
    public int Age { get; set; }
}
public partial class UserModel
{
    public int Id { get; set; }

    [JsonPropertyName("user_name")]
    public string Name { get; set; }

    public string ExtraInfo { get; set; }
}

public static class Utils
{
    public static string SerializeExtraInfo(ExtraInfo extraInfo)
    {
        return JsonSerializer.Serialize(extraInfo, SerializerContext.Default.ExtraInfo);
    }
}

