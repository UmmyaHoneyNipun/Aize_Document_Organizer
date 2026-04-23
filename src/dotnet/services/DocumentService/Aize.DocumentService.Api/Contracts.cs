using Aize.DocumentService.Application;

namespace Aize.DocumentService.Api;

public sealed record UploadDocumentsResponse(IReadOnlyCollection<AcceptedDocumentDto> Documents);

public sealed record CompleteDocumentProcessingApiRequest(
    Guid DocumentId,
    IReadOnlyCollection<HotspotDto> Hotspots);

public sealed record FailDocumentProcessingApiRequest(
    Guid DocumentId,
    string Reason);
