# P&ID Document Organizer

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
- simulates OCR/CV extraction for structured P&ID elements, tags, and overlay geometry
- calls the .NET API back with completion or failure payloads

### 3. React portal

`src/web/pid-portal`

Responsibilities:

- bulk upload multiple drawings
- show accepted `202` uploads immediately
- subscribe to realtime status updates through SignalR
- show completed overlay counts per drawing

## Local development

### Build everything with one Dockerfile

From the repository root:

```bash
docker build -t aize-document-organizer .
docker run --rm -p 5050:8080 -p 8001:8001 aize-document-organizer
```

This single image:

- serves the React frontend from the .NET app at `http://localhost:5050`
- serves Swagger at `http://localhost:5050/swagger`
- runs the Python processor inside the same container on `http://localhost:8001`

This is the simplest local/dev packaging path. It intentionally collapses the microservices into one container for convenience.

### Start the .NET service

```bash
dotnet run --project src/dotnet/services/DocumentService/Aize.DocumentService.Api
```

The API defaults to `http://localhost:5000` when launched through typical local tooling. The Python processor base URL is configured in `appsettings.json`.

Swagger UI is available at:

- `http://localhost:5000/swagger`
- `https://localhost:7000/swagger`

Authentication:

- `POST /api/auth/login` issues a local bearer token for development
- `GET /api/auth/me` returns the authenticated user profile
- protected endpoints require `Authorization: Bearer <token>`

Demo users:

- `demo.operator / Pass123!`
- `demo.admin / Admin123!`

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

### Start backend services with Docker

```bash
docker compose -f deploy/docker/docker-compose.yml up --build
```

Docker now starts the full stack with safer host ports by default:

- .NET API: `http://localhost:5050`
- Python processor: `http://localhost:8002`
- Swagger UI: `http://localhost:5050/swagger`
- React portal: `http://localhost:5173`

Use this when you want the services split across separate containers instead of the single-image local build above.

When using Swagger:

1. Call `POST /api/auth/login`
2. Copy the `accessToken`
3. Click `Authorize`
4. Paste the raw `accessToken`

You can still override the Docker host ports if you want:

```bash
DOCUMENT_API_PORT=5051 PYTHON_PROCESSOR_PORT=8003 FRONTEND_PORT=5174 docker compose -f deploy/docker/docker-compose.yml up --build
```

## HTTP endpoints

### Upload drawings

`POST /api/documents/uploads`

Multipart form fields:

- `projectName`
- one or more `files`

The response is `202 Accepted` with accepted document IDs.

### Query status

- `GET /api/documents`
- `GET /api/documents/{documentId}`

### Processing callbacks

- `POST /api/processing/completed`
- `POST /api/processing/failed`

### SignalR hub

`/hubs/documents`

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
