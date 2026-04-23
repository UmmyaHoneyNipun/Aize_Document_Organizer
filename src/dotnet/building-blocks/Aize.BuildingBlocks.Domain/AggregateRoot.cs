namespace Aize.BuildingBlocks.Domain;

public abstract class AggregateRoot<TId>(TId id) : Entity<TId>(id)
    where TId : notnull;
