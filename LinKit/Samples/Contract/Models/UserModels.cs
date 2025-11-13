using System.Text.Json;
using System.Text.Json.Serialization;
using LinKit.Core.BackgroundJobs;
using LinKit.Core.Cqrs;
using LinKit.Grpc;
using SampleWebApp.Grpc.Users;

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

//[GrpcClient(typeof(UserGrpcService.UserGrpcServiceClient), "UpdateUserAsync")]
//[MapTo(typeof(UserModel))]
public partial class UpdateUser : ICommand
{
    public int Id { get; set; }

    [JsonPropertyName("user_name")]
    public string UserName { get; set; }
    public string? Name { get; set; }
    public ExtraInfo ExtraInfo { get; set; }
    public Model1 Model { get; set; }
    public List<Model1> Models { get; set; }
}

public class ExtraInfo
{
    public int Age { get; set; }
}

public partial class UserModel
{
    public int? Id { get; set; }

    [JsonPropertyName("user_name")]
    public string? Name { get; set; }

    public string ExtraInfo { get; set; }
    public Model2 Model { get; set; }
    public List<Model2> Models { get; set; }
}

public class Model1
{
    public int Id { get; set; }
    public string Prop1 { get; set; }
}

public class Model2
{
    public int Id { get; set; }
    public string Prop1 { get; set; }
}

public static class Utils
{
    public static string SerializeExtraInfo(ExtraInfo extraInfo)
    {
        return JsonSerializer.Serialize(extraInfo, SerializerContext.Default.ExtraInfo);
    }
}
