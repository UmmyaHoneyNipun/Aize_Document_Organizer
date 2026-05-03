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

public sealed record PidBoundingBoxDto(
    double X,
    double Y,
    double Width,
    double Height);

public sealed record PidPointDto(
    double X,
    double Y);

public sealed record PidElementConfidenceDto(
    double Ocr,
    double Detection,
    double Overall);

public sealed record PidElementAttributeDto(
    string Name,
    string Value);

public sealed record PidElementRelationDto(
    string Type,
    string TargetElementId);

public sealed record PidElementDto(
    string ElementId,
    string Kind,
    string? Subtype,
    string RawText,
    string NormalizedText,
    PidBoundingBoxDto BboxPx,
    PidBoundingBoxDto BboxNorm,
    IReadOnlyCollection<PidPointDto> Polygon,
    PidElementConfidenceDto Confidence,
    IReadOnlyCollection<PidElementAttributeDto> Attributes,
    IReadOnlyCollection<PidElementRelationDto> Relations);

public sealed record PidPageAnalysisDto(
    int PageNumber,
    int ImageWidth,
    int ImageHeight,
    IReadOnlyCollection<PidElementDto> Elements);

public sealed record PidSourceAssetDto(
    string BlobPath,
    string ContentType,
    int PageCount,
    int? ImageWidth,
    int? ImageHeight);

public sealed record PidAnalysisSummaryDto(
    int TagCount,
    int EquipmentCount,
    int InstrumentCount,
    int LineNumberCount);

public sealed record PidAnalysisDto(
    int SchemaVersion,
    string ProcessorVersion,
    PidSourceAssetDto Source,
    PidAnalysisSummaryDto Summary,
    IReadOnlyCollection<PidPageAnalysisDto> Pages);

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
    IReadOnlyCollection<HotspotDto> Hotspots,
    PidAnalysisDto? Analysis);

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
    PidAnalysisDto Analysis);

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
    IReadOnlyCollection<HotspotDto> Hotspots,
    PidAnalysisDto? Analysis);
