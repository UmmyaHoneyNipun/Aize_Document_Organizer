using Aize.BuildingBlocks.Domain;

namespace Aize.DocumentService.Domain;

public sealed class Hotspot : ValueObject
{
    public Hotspot(string tagNumber, double x, double y, double width, double height, double confidence)
    {
        if (string.IsNullOrWhiteSpace(tagNumber))
        {
            throw new ArgumentException("Hotspot tag number is required.", nameof(tagNumber));
        }

        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Hotspot dimensions must be greater than zero.");
        }

        TagNumber = tagNumber;
        X = x;
        Y = y;
        Width = width;
        Height = height;
        Confidence = confidence;
    }

    public string TagNumber { get; }

    public double X { get; }

    public double Y { get; }

    public double Width { get; }

    public double Height { get; }

    public double Confidence { get; }

    protected override IEnumerable<object?> GetAtomicValues()
    {
        yield return TagNumber;
        yield return X;
        yield return Y;
        yield return Width;
        yield return Height;
        yield return Confidence;
    }
}
