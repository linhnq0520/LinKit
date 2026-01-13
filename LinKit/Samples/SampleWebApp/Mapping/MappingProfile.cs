using LinKit.Core.Mapping;
using LinKit.Json.Runtime;
using SampleWebApp.Domains;
using SampleWebApp.Features.Users.UpdateUser;

namespace SampleWebApp.Mapping
{
    [MapperContext]
    public partial class MappingProfile : IMappingConfigurator
    {
        public void Configure(IMapperConfigurationBuilder builder)
        {
            builder
                .CreateMap<UpdateUserCommand, User>()
                .ForMember(d => d.Name, o => o.MapFrom(s => s.Id.ToJson(null, null)))
                .ForMember(d => d.Id, o => o.Ignore());
            builder.CreateMap<User, UpdateUserResposne>();
            builder.CreateMap<User, User1>();
        }
    }
}
