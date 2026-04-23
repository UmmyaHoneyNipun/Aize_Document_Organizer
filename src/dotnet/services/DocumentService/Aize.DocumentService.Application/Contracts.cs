using Aize.DocumentService.Domain;

namespace Aize.DocumentService.Application;

public sealed record UploadDocumentPayload(
    string ProjectName,
    string UploadedByUserId,
    string OriginalFileName,
    string ContentType,
    long Length,
    Stream Content);

public sealed record AcceptedDocumentDto(
    Guid DocumentId,
    string ProjectName,
    string OriginalFileName,
    DocumentStatus Status,
    string BlobPath,
    DateTimeOffset UploadedAtUtc);

public sealed record HotspotDto(
    string TagNumber,
    double X,
    double Y,
    double Width,
    double Height,
    double Confidence);

public sealed record DocumentDetailsDto(
    Guid DocumentId,
    string ProjectName,
    string OriginalFileName,
    string ContentType,
    string UploadedByUserId,
    DocumentStatus Status,
    string BlobPath,
    int HotspotCount,
    string? FailureReason,
    DateTimeOffset UploadedAtUtc,
    DateTimeOffset LastUpdatedAtUtc,
    IReadOnlyCollection<HotspotDto> Hotspots);

public sealed record DocumentUploadedMessage(
    Guid DocumentId,
    string ProjectName,
    string OriginalFileName,
    string ContentType,
    string UploadedByUserId,
    string BlobPath,
    DateTimeOffset UploadedAtUtc,
    int SchemaVersion,
    Guid CorrelationId);

public sealed record CompleteDocumentProcessingRequest(
    Guid DocumentId,
    IReadOnlyCollection<HotspotDto> Hotspots);

public sealed record FailDocumentProcessingRequest(
    Guid DocumentId,
    string Reason);

public sealed record ObjectStorageSaveRequest(
    Guid DocumentId,
    string OriginalFileName,
    string ContentType);

public sealed record ObjectStorageSaveResult(
    string BlobPath,
    long ContentLength);

public sealed record DocumentRealtimeMessage(
    Guid DocumentId,
    DocumentStatus Status,
    string? FailureReason,
    int HotspotCount,
    DateTimeOffset LastUpdatedAtUtc);

public sealed record DocumentReadModel(
    Guid DocumentId,
    string ProjectName,
    string OriginalFileName,
    string ContentType,
    string UploadedByUserId,
    string BlobPath,
    DocumentStatus Status,
    int HotspotCount,
    string? FailureReason,
    DateTimeOffset UploadedAtUtc,
    DateTimeOffset LastUpdatedAtUtc,
    IReadOnlyCollection<HotspotDto> Hotspots);
