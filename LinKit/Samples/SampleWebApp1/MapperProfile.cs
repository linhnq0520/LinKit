using Contract.Models;
using LinKit.Core.Mapping;
using SampleWebApp1.Features;

namespace SampleWebApp1
{
    [MapperContext]
    public partial class ApplicationMapperContext : IMappingConfigurator
    {
        public void Configure(IMapperConfigurationBuilder builder)
        {
            builder.CreateMap<GetUserQuery, GetUserById>()
                ;

        }
    }
}
