using LinKit.Core.Mapping;
using SampleWebApp.Domains;
using SampleWebApp.Features.Users.UpdateUser;

namespace SampleWebApp.Mapping
{
    [MapperContext]
    public partial class MappingProfile : IMappingConfigurator
    {
        public void Configure(IMapperConfigurationBuilder builder)
        {
            builder.CreateMap<UpdateUserCommand, User>();
            builder.CreateMap<User, UpdateUserResposne>();
        }
    }
}
