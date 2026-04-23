using Aize.DocumentService.Domain;

namespace Aize.DocumentService.Application;

public interface IObjectStorage
{
    Task<ObjectStorageSaveResult> SaveAsync(
        Stream content,
        ObjectStorageSaveRequest request,
        CancellationToken cancellationToken);
}

public interface IDocumentMessagePublisher
{
    Task PublishAsync(DocumentUploadedMessage message, CancellationToken cancellationToken);
}

public interface IDocumentRepository
{
    Task AddAsync(Document document, CancellationToken cancellationToken);

    Task UpdateAsync(Document document, CancellationToken cancellationToken);

    Task<Document?> GetAsync(Guid documentId, CancellationToken cancellationToken);
}

public interface IDocumentReadModelRepository
{
    Task UpsertAsync(DocumentReadModel readModel, CancellationToken cancellationToken);

    Task<DocumentReadModel?> GetAsync(Guid documentId, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<DocumentReadModel>> ListByUserAsync(string? uploadedByUserId, CancellationToken cancellationToken);
}

public interface IDocumentProcessingQueue
{
    ValueTask EnqueueAsync(DocumentUploadedMessage message, CancellationToken cancellationToken);

    IAsyncEnumerable<DocumentUploadedMessage> DequeueAllAsync(CancellationToken cancellationToken);
}

public interface IDocumentNotificationPublisher
{
    Task PublishStatusChangedAsync(DocumentReadModel readModel, CancellationToken cancellationToken);
}

public interface IPythonProcessorClient
{
    Task DispatchAsync(DocumentUploadedMessage message, CancellationToken cancellationToken);
}
