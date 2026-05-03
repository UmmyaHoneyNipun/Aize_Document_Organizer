using Aize.BuildingBlocks.Application;
using Aize.DocumentService.Domain;

namespace Aize.DocumentService.Application;

public sealed class DocumentApplicationService
{
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentReadModelRepository _readModelRepository;
    private readonly IObjectStorage _objectStorage;
    private readonly IDocumentMessagePublisher _messagePublisher;
    private readonly IDocumentNotificationPublisher _notificationPublisher;
    private readonly TimeProvider _timeProvider;

    public DocumentApplicationService(
        IDocumentRepository documentRepository,
        IDocumentReadModelRepository readModelRepository,
        IObjectStorage objectStorage,
        IDocumentMessagePublisher messagePublisher,
        IDocumentNotificationPublisher notificationPublisher,
        TimeProvider timeProvider)
    {
        _documentRepository = documentRepository;
        _readModelRepository = readModelRepository;
        _objectStorage = objectStorage;
        _messagePublisher = messagePublisher;
        _notificationPublisher = notificationPublisher;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyCollection<AcceptedDocumentDto>> UploadAsync(
        IReadOnlyCollection<UploadDocumentPayload> payloads,
        CancellationToken cancellationToken)
    {
        var accepted = new List<AcceptedDocumentDto>(payloads.Count);

        foreach (var payload in payloads)
        {
            var documentId = Guid.NewGuid();
            var uploadedAtUtc = _timeProvider.GetUtcNow();
            var storageResult = await _objectStorage.SaveAsync(
                payload.Content,
                new ObjectStorageSaveRequest(documentId, payload.OriginalFileName, payload.ContentType),
                cancellationToken);

            var document = Document.Create(
                documentId,
                payload.ProjectName,
                payload.OriginalFileName,
                payload.ContentType,
                payload.UploadedByUserId,
                storageResult.BlobPath,
                uploadedAtUtc);

            await _documentRepository.AddAsync(document, cancellationToken);

            var readModel = ToReadModel(document, analysis: null);
            await _readModelRepository.UpsertAsync(readModel, cancellationToken);

            var message = new DocumentUploadedMessage(
                document.Id,
                document.ProjectName,
                document.OriginalFileName,
                document.ContentType,
                document.UploadedByUserId,
                document.BlobPath,
                uploadedAtUtc,
                SchemaVersion: 1,
                CorrelationId: Guid.NewGuid());

            await _messagePublisher.PublishAsync(message, cancellationToken);

            accepted.Add(new AcceptedDocumentDto(
                document.Id,
                document.ProjectName,
                document.OriginalFileName,
                document.Status,
                document.BlobPath,
                document.UploadedAtUtc));
        }

        return accepted;
    }

    public async Task<Result> MarkProcessingAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var document = await _documentRepository.GetAsync(documentId, cancellationToken);
        if (document is null)
        {
            return Result.Failure($"Document '{documentId}' was not found.");
        }

        document.MarkProcessing(_timeProvider.GetUtcNow());
        await _documentRepository.UpdateAsync(document, cancellationToken);

        var existingReadModel = await _readModelRepository.GetAsync(documentId, cancellationToken);
        var readModel = ToReadModel(document, existingReadModel?.Analysis);
        await _readModelRepository.UpsertAsync(readModel, cancellationToken);
        await _notificationPublisher.PublishStatusChangedAsync(readModel, cancellationToken);

        return Result.Success();
    }

    public async Task<Result> CompleteProcessingAsync(
        CompleteDocumentProcessingRequest request,
        CancellationToken cancellationToken)
    {
        var document = await _documentRepository.GetAsync(request.DocumentId, cancellationToken);
        if (document is null)
        {
            return Result.Failure($"Document '{request.DocumentId}' was not found.");
        }

        var hotspots = ExtractHotspots(request.Analysis)
            .Select(hotspot => new Hotspot(
                hotspot.TagNumber,
                hotspot.X,
                hotspot.Y,
                hotspot.Width,
                hotspot.Height,
                hotspot.Confidence))
            .ToArray();

        document.Complete(hotspots, _timeProvider.GetUtcNow());
        await _documentRepository.UpdateAsync(document, cancellationToken);

        var readModel = ToReadModel(document, request.Analysis);
        await _readModelRepository.UpsertAsync(readModel, cancellationToken);
        await _notificationPublisher.PublishStatusChangedAsync(readModel, cancellationToken);

        return Result.Success();
    }

    public async Task<Result> FailProcessingAsync(
        FailDocumentProcessingRequest request,
        CancellationToken cancellationToken)
    {
        var document = await _documentRepository.GetAsync(request.DocumentId, cancellationToken);
        if (document is null)
        {
            return Result.Failure($"Document '{request.DocumentId}' was not found.");
        }

        document.Fail(request.Reason, _timeProvider.GetUtcNow());
        await _documentRepository.UpdateAsync(document, cancellationToken);

        var existingReadModel = await _readModelRepository.GetAsync(request.DocumentId, cancellationToken);
        var readModel = ToReadModel(document, existingReadModel?.Analysis);
        await _readModelRepository.UpsertAsync(readModel, cancellationToken);
        await _notificationPublisher.PublishStatusChangedAsync(readModel, cancellationToken);

        return Result.Success();
    }

    public async Task<DocumentDetailsDto?> GetAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var readModel = await _readModelRepository.GetAsync(documentId, cancellationToken);
        return readModel is null ? null : ToDetails(readModel);
    }

    public async Task<IReadOnlyCollection<DocumentDetailsDto>> ListByUserAsync(
        string? uploadedByUserId,
        CancellationToken cancellationToken)
    {
        var readModels = await _readModelRepository.ListByUserAsync(uploadedByUserId, cancellationToken);
        return readModels
            .OrderByDescending(document => document.UploadedAtUtc)
            .Select(ToDetails)
            .ToArray();
    }

    private static DocumentDetailsDto ToDetails(DocumentReadModel readModel) =>
        new(
            readModel.DocumentId,
            readModel.ProjectName,
            readModel.OriginalFileName,
            readModel.ContentType,
            readModel.UploadedByUserId,
            readModel.Status,
            readModel.BlobPath,
            readModel.HotspotCount,
            readModel.FailureReason,
            readModel.UploadedAtUtc,
            readModel.LastUpdatedAtUtc,
            readModel.Hotspots,
            readModel.Analysis);

    private static DocumentReadModel ToReadModel(Document document, PidAnalysisDto? analysis) =>
        new(
            document.Id,
            document.ProjectName,
            document.OriginalFileName,
            document.ContentType,
            document.UploadedByUserId,
            document.BlobPath,
            document.Status,
            document.HotspotCount,
            document.FailureReason,
            document.UploadedAtUtc,
            document.LastUpdatedAtUtc,
            document.Hotspots
                .Select(hotspot => new HotspotDto(
                    hotspot.TagNumber,
                    hotspot.X,
                    hotspot.Y,
                    hotspot.Width,
                    hotspot.Height,
                    hotspot.Confidence))
                .ToArray(),
            analysis);

    private static IReadOnlyCollection<HotspotDto> ExtractHotspots(PidAnalysisDto analysis) =>
        analysis.Pages
            .SelectMany(page => page.Elements)
            .Select(element => new HotspotDto(
                string.IsNullOrWhiteSpace(element.NormalizedText) ? element.RawText : element.NormalizedText,
                element.BboxPx.X,
                element.BboxPx.Y,
                element.BboxPx.Width,
                element.BboxPx.Height,
                element.Confidence.Overall))
            .ToArray();

}
