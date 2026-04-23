using Aize.BuildingBlocks.Domain;

namespace Aize.DocumentService.Domain;

public sealed class Document : AggregateRoot<Guid>
{
    private readonly List<Hotspot> _hotspots = [];

    private Document(
        Guid id,
        string projectName,
        string originalFileName,
        string contentType,
        string uploadedByUserId,
        string blobPath,
        DateTimeOffset uploadedAtUtc)
        : base(id)
    {
        ProjectName = projectName;
        OriginalFileName = originalFileName;
        ContentType = contentType;
        UploadedByUserId = uploadedByUserId;
        BlobPath = blobPath;
        UploadedAtUtc = uploadedAtUtc;
        LastUpdatedAtUtc = uploadedAtUtc;
        Status = DocumentStatus.Pending;
    }

    public string ProjectName { get; private set; }

    public string OriginalFileName { get; private set; }

    public string ContentType { get; private set; }

    public string UploadedByUserId { get; private set; }

    public string BlobPath { get; private set; }

    public DocumentStatus Status { get; private set; }

    public DateTimeOffset UploadedAtUtc { get; private set; }

    public DateTimeOffset LastUpdatedAtUtc { get; private set; }

    public int HotspotCount => _hotspots.Count;

    public string? FailureReason { get; private set; }

    public IReadOnlyCollection<Hotspot> Hotspots => _hotspots.AsReadOnly();

    public static Document Create(
        Guid documentId,
        string projectName,
        string originalFileName,
        string contentType,
        string uploadedByUserId,
        string blobPath,
        DateTimeOffset uploadedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(projectName))
        {
            throw new ArgumentException("Project name is required.", nameof(projectName));
        }

        if (string.IsNullOrWhiteSpace(originalFileName))
        {
            throw new ArgumentException("Original file name is required.", nameof(originalFileName));
        }

        if (string.IsNullOrWhiteSpace(uploadedByUserId))
        {
            throw new ArgumentException("Uploaded by user id is required.", nameof(uploadedByUserId));
        }

        if (string.IsNullOrWhiteSpace(blobPath))
        {
            throw new ArgumentException("Blob path is required.", nameof(blobPath));
        }

        var document = new Document(
            documentId,
            projectName.Trim(),
            originalFileName.Trim(),
            string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType.Trim(),
            uploadedByUserId.Trim(),
            blobPath,
            uploadedAtUtc);

        document.Raise(new DocumentUploadedDomainEvent(document.Id, blobPath, uploadedAtUtc));
        return document;
    }

    public void MarkProcessing(DateTimeOffset updatedAtUtc)
    {
        if (Status == DocumentStatus.Completed)
        {
            return;
        }

        Status = DocumentStatus.Processing;
        FailureReason = null;
        LastUpdatedAtUtc = updatedAtUtc;
    }

    public void Complete(IEnumerable<Hotspot> hotspots, DateTimeOffset updatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(hotspots);

        _hotspots.Clear();
        _hotspots.AddRange(hotspots);
        Status = DocumentStatus.Completed;
        FailureReason = null;
        LastUpdatedAtUtc = updatedAtUtc;
    }

    public void Fail(string reason, DateTimeOffset updatedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("Failure reason is required.", nameof(reason));
        }

        Status = DocumentStatus.Failed;
        FailureReason = reason.Trim();
        LastUpdatedAtUtc = updatedAtUtc;
    }
}
