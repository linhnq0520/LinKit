using Contract.Models;
using LinKit.Core.Mapping;

namespace Contract
{
    [MapperContext]
    public partial class ApplicationMapperContext : IMappingConfigurator
    {
        public void Configure(IMapperConfigurationBuilder builder)
        {
            builder.CreateMap<UpdateUser, UserModel>();                // Rule #2
                //.ForMember("ExtraInfo", typeof(Utils), "SerializeExtraInfo", nameof(UpdateUser.ExtraInfo));
                //.ForMember(nameof(UserModel.Id), MappingRules.Ignore);

        }
    }
}
