using Aize.BuildingBlocks.Domain;

namespace Aize.DocumentService.Domain;

public sealed record DocumentUploadedDomainEvent(Guid DocumentId, string BlobPath, DateTimeOffset OccurredOnUtc) : IDomainEvent;
