namespace Aize.BuildingBlocks.Domain;

public interface IDomainEvent
{
    DateTimeOffset OccurredOnUtc { get; }
}
