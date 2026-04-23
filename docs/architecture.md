# Architecture Overview

This repository models the pipeline as two microservices:

1. `Aize.DocumentService.Api` in .NET
2. `document_processor` in Python

The .NET service owns the clean-architecture layers:

- `Domain`: aggregate root and value objects for a drawing as it moves from pending to completed or failed.
- `Application`: ports plus use cases for upload, status changes, and realtime notifications.
- `Infrastructure`: local adapters that simulate blob storage, queueing, and SignalR broadcasting.
- `Api`: HTTP ingress endpoints, processing callbacks, and the SignalR hub.

The Python service is intentionally narrow:

- accepts a `DocumentUploadedMessage`
- simulates OCR/CV extraction
- posts completion or failure back to the .NET service

## Production mapping

The current code uses local development adapters so the solution stays self-contained in a network-restricted workspace.

Replace these adapters for production:

- `FileSystemObjectStorage` -> Azure Blob Storage
- `InMemoryDocumentMessagePublisher` and `InMemoryDocumentProcessingQueue` -> Azure Service Bus or RabbitMQ
- `InMemoryDocumentRepository` -> PostgreSQL
- `InMemoryDocumentReadModelRepository` -> PostgreSQL + MongoDB split read/write model
- `SignalRDocumentNotificationPublisher` -> managed SignalR service or the same hub behind Redis backplane

## Request lifecycle

1. React posts multiple drawings to `/api/documents/uploads`.
2. The .NET API streams each file into local blob storage and stores the document state as `Pending`.
3. The API publishes a `DocumentUploadedMessage` and returns `202 Accepted`.
4. A background dispatcher marks the document as `Processing` and calls the Python processor.
5. The Python processor returns extracted hotspots through `/api/processing/completed`.
6. The .NET service marks the document `Completed` and pushes a SignalR update to the owning user.
