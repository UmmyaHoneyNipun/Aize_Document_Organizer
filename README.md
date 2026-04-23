# Aize Document Organizer

An Aize-inspired P&ID document pipeline built around clean architecture and DDD in .NET, with a Python processing microservice for OCR/CV-style extraction.

## Services

### 1. .NET document service

`src/dotnet/services/DocumentService/Aize.DocumentService.Api`

Responsibilities:

- accepts document uploads
- streams raw files into blob-style storage
- stores document status as `Pending`, `Processing`, `Completed`, or `Failed`
- publishes upload events
- receives processing callbacks
- broadcasts realtime updates through SignalR

Architecture:

- `Domain`: aggregate root, status transitions, hotspot value object
- `Application`: use cases and ports
- `Infrastructure`: local adapters for storage, queueing, and notifications
- `Api`: ingress endpoints and hub

### 2. Python document processor

`src/python/document_processor`

Responsibilities:

- receives upload events from the .NET queue dispatcher
- simulates OCR/CV extraction for hotspots and tag numbers
- calls the .NET API back with completion or failure payloads

### 3. React portal

`src/web/pid-portal`

Responsibilities:

- bulk upload multiple drawings
- show accepted `202` uploads immediately
- subscribe to realtime status updates through SignalR
- show completed hotspot counts per drawing

## Local development

### Start the .NET service

```bash
dotnet run --project src/dotnet/services/DocumentService/Aize.DocumentService.Api
```

The API defaults to `http://localhost:5000` when launched through typical local tooling. The Python processor base URL is configured in `appsettings.json`.

### Start the Python processor

```bash
cd src/python/document_processor
python3 -m venv .venv
source .venv/bin/activate
pip install -r requirements.txt
uvicorn app.main:app --reload --port 8001
```

### Start the React app

```bash
cd src/web/pid-portal
npm install
npm run dev
```

If needed, point the frontend at a different API URL:

```bash
VITE_DOCUMENT_API_BASE_URL=http://localhost:5000 npm run dev
```

## HTTP endpoints

### Upload drawings

`POST /api/documents/uploads`

Multipart form fields:

- `projectName`
- `uploadedByUserId`
- one or more `files`

The response is `202 Accepted` with accepted document IDs.

### Query status

- `GET /api/documents`
- `GET /api/documents/{documentId}`

### Processing callbacks

- `POST /api/processing/completed`
- `POST /api/processing/failed`

### SignalR hub

`/hubs/documents?userId=demo.user`

## Deployment assets

- Docker Compose: [deploy/docker/docker-compose.yml](/Users/ummya/Documents/GitHub/Aize_Document_Organizer/deploy/docker/docker-compose.yml)
- Kubernetes manifests: [deploy/k8s/namespace.yaml](/Users/ummya/Documents/GitHub/Aize_Document_Organizer/deploy/k8s/namespace.yaml)
- Architecture notes: [docs/architecture.md](/Users/ummya/Documents/GitHub/Aize_Document_Organizer/docs/architecture.md)
- Queue contract: [docs/service-bus-contracts.md](/Users/ummya/Documents/GitHub/Aize_Document_Organizer/docs/service-bus-contracts.md)

## Important note about persistence and queueing

To keep this repository self-contained in a network-restricted workspace, the current infrastructure uses local development adapters:

- file system storage instead of Azure Blob Storage
- in-memory queueing instead of Azure Service Bus / RabbitMQ
- in-memory repositories instead of PostgreSQL / MongoDB

The production replacements are documented in [docs/architecture.md](/Users/ummya/Documents/GitHub/Aize_Document_Organizer/docs/architecture.md), and the deployment manifests are shaped around the production topology you described.
