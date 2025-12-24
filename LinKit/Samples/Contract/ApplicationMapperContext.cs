using Contract.Models;
using LinKit.Core.Mapping;
using LinKit.Json.Runtime;

namespace Contract;

[MapperContext]
public partial class ApplicationMapperContext : IMappingConfigurator
{
    public void Configure(IMapperConfigurationBuilder builder)
    {
        builder
            .CreateMap<UpdateUser, UserModel>()
            .ForMember(dest => dest.Name, opt => opt.Ignore())
            .ForMember(
                dest => dest.Name,
                opt => opt.MapFrom(src => src.Models[0].ToJson(null, null))
            )
            .ForMember(
                dest => dest.ExtraInfo,
                opt =>
                    opt.ConvertWith(
                        typeof(Utils),
                        nameof(Utils.SerializeExtraInfo),
                        src => src.ExtraInfo
                    )
            );

        builder.CreateMap<Model1, Model2>();
    }
}
