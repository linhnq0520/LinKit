using LinKit.Data;

namespace MonolithSample.BuildingBlocks.Domain;

public abstract class BaseEntity
{
    [PrimaryKey(autoIncrement: true)]
    public int Id { get; set; }
}
