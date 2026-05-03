using Aize.DocumentService.Application;
using Microsoft.AspNetCore.Mvc;

namespace Aize.DocumentService.Api;

public sealed class UploadDocumentsApiRequest
{
    [FromForm(Name = "projectName")]
    public string ProjectName { get; init; } = string.Empty;

    [FromForm(Name = "files")]
    public List<IFormFile> Files { get; init; } = [];
}

public sealed record UploadDocumentsResponse(IReadOnlyCollection<AcceptedDocumentDto> Documents);

public sealed record CompleteDocumentProcessingApiRequest(
    Guid DocumentId,
    PidAnalysisDto Analysis);

public sealed record FailDocumentProcessingApiRequest(
    Guid DocumentId,
    string Reason);
