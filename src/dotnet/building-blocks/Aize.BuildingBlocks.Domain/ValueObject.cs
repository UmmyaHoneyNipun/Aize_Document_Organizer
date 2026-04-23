namespace Aize.BuildingBlocks.Domain;

public abstract class ValueObject
{
    protected abstract IEnumerable<object?> GetAtomicValues();

    public override bool Equals(object? obj)
    {
        if (obj is null || obj.GetType() != GetType())
        {
            return false;
        }

        return GetAtomicValues().SequenceEqual(((ValueObject)obj).GetAtomicValues());
    }

    public override int GetHashCode()
    {
        return GetAtomicValues()
            .Select(value => value?.GetHashCode() ?? 0)
            .Aggregate(17, (current, hashCode) => (current * 31) + hashCode);
    }
}
