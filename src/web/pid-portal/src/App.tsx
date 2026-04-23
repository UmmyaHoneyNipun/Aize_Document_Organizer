import { useEffect, useMemo, useState } from "react";
import * as signalR from "@microsoft/signalr";

type DocumentStatus = "Pending" | "Processing" | "Completed" | "Failed";

type Hotspot = {
  tagNumber: string;
  x: number;
  y: number;
  width: number;
  height: number;
  confidence: number;
};

type DocumentItem = {
  documentId: string;
  projectName: string;
  originalFileName: string;
  contentType: string;
  uploadedByUserId: string;
  status: DocumentStatus;
  blobPath: string;
  hotspotCount: number;
  failureReason?: string | null;
  uploadedAtUtc: string;
  lastUpdatedAtUtc: string;
  hotspots: Hotspot[];
};

type UploadResponse = {
  documents: Array<{
    documentId: string;
    projectName: string;
    originalFileName: string;
    status: DocumentStatus;
    blobPath: string;
    uploadedAtUtc: string;
  }>;
};

const apiBaseUrl = import.meta.env.VITE_DOCUMENT_API_BASE_URL ?? "http://localhost:5000";

function App() {
  const [projectName, setProjectName] = useState("North Sea Compression");
  const [uploadedByUserId, setUploadedByUserId] = useState("demo.user");
  const [selectedFiles, setSelectedFiles] = useState<FileList | null>(null);
  const [documents, setDocuments] = useState<DocumentItem[]>([]);
  const [isUploading, setIsUploading] = useState(false);
  const [feedMessage, setFeedMessage] = useState("Idle");

  const connectedStatuses = useMemo(
    () => ({
      Pending: 0,
      Processing: 0,
      Completed: 0,
      Failed: 0
    }),
    []
  );

  useEffect(() => {
    void refreshDocuments(uploadedByUserId);
  }, [uploadedByUserId]);

  useEffect(() => {
    const connection = new signalR.HubConnectionBuilder()
      .withUrl(`${apiBaseUrl}/hubs/documents?userId=${encodeURIComponent(uploadedByUserId)}`)
      .withAutomaticReconnect()
      .build();

    connection.on("DocumentUpdated", () => {
      void refreshDocuments(uploadedByUserId);
      setFeedMessage("Realtime status received from SignalR");
    });

    void connection.start().catch(() => {
      setFeedMessage("SignalR not connected yet");
    });

    return () => {
      void connection.stop();
    };
  }, [uploadedByUserId]);

  async function refreshDocuments(userId: string) {
    const response = await fetch(`${apiBaseUrl}/api/documents?uploadedByUserId=${encodeURIComponent(userId)}`);
    if (!response.ok) {
      setFeedMessage("Could not refresh document list");
      return;
    }

    const payload = (await response.json()) as DocumentItem[];
    setDocuments(payload);
  }

  async function handleUpload(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!selectedFiles?.length) {
      setFeedMessage("Pick at least one drawing first");
      return;
    }

    const formData = new FormData();
    formData.set("projectName", projectName);
    formData.set("uploadedByUserId", uploadedByUserId);

    Array.from(selectedFiles).forEach((file) => {
      formData.append("files", file);
    });

    setIsUploading(true);
    setFeedMessage(`Dispatching ${selectedFiles.length} drawing(s) to ingestion`);

    const response = await fetch(`${apiBaseUrl}/api/documents/uploads`, {
      method: "POST",
      body: formData
    });

    setIsUploading(false);

    if (!response.ok) {
      setFeedMessage("Upload failed");
      return;
    }

    const payload = (await response.json()) as UploadResponse;
    setFeedMessage(`${payload.documents.length} drawing(s) accepted with HTTP 202`);
    await refreshDocuments(uploadedByUserId);
  }

  const totals = documents.reduce(
    (accumulator, document) => {
      accumulator[document.status] += 1;
      return accumulator;
    },
    { ...connectedStatuses }
  );

  return (
    <main className="shell">
      <section className="hero">
        <p className="eyebrow">Aize-inspired document pipeline</p>
        <h1>Upload hundreds of P&amp;IDs without pinning the UI.</h1>
        <p className="lede">
          The .NET ingestion service accepts the files, returns 202 immediately, and the Python processor
          resolves hotspots asynchronously through the realtime status stream.
        </p>
      </section>

      <section className="metrics">
        <article>
          <span>Pending</span>
          <strong>{totals.Pending}</strong>
        </article>
        <article>
          <span>Processing</span>
          <strong>{totals.Processing}</strong>
        </article>
        <article>
          <span>Completed</span>
          <strong>{totals.Completed}</strong>
        </article>
        <article>
          <span>Failed</span>
          <strong>{totals.Failed}</strong>
        </article>
      </section>

      <section className="grid">
        <form className="panel upload-panel" onSubmit={handleUpload}>
          <div className="panel-heading">
            <h2>Ingress</h2>
            <p>{feedMessage}</p>
          </div>

          <label>
            Project
            <input value={projectName} onChange={(event) => setProjectName(event.target.value)} />
          </label>

          <label>
            User
            <input value={uploadedByUserId} onChange={(event) => setUploadedByUserId(event.target.value)} />
          </label>

          <label className="drop-zone">
            <input
              type="file"
              multiple
              accept=".pdf,.png,.jpg,.jpeg"
              onChange={(event) => setSelectedFiles(event.target.files)}
            />
            <span>{selectedFiles?.length ? `${selectedFiles.length} files selected` : "Drop drawings or browse files"}</span>
          </label>

          <button disabled={isUploading} type="submit">
            {isUploading ? "Streaming to blob storage..." : "Upload drawings"}
          </button>
        </form>

        <section className="panel">
          <div className="panel-heading">
            <h2>Realtime documents</h2>
            <p>{documents.length} tracked item(s)</p>
          </div>

          <div className="document-list">
            {documents.map((document) => (
              <article className={`document-card status-${document.status.toLowerCase()}`} key={document.documentId}>
                <div>
                  <p className="file-name">{document.originalFileName}</p>
                  <p className="meta-line">{document.projectName}</p>
                </div>
                <div className="status-badge">{document.status}</div>
                <p className="meta-line">{new Date(document.lastUpdatedAtUtc).toLocaleString()}</p>
                <p className="meta-line">Hotspots: {document.hotspotCount}</p>
                {document.failureReason ? <p className="error-copy">{document.failureReason}</p> : null}
              </article>
            ))}
          </div>
        </section>
      </section>
    </main>
  );
}

export default App;
