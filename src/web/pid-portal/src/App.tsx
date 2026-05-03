import { useEffect, useMemo, useState } from "react";
import * as signalR from "@microsoft/signalr";

type DocumentStatus = "Pending" | "Processing" | "Completed" | "Failed";
type DocumentStatusWire = DocumentStatus | 0 | 1 | 2 | 3;

type Hotspot = {
  tagNumber: string;
  x: number;
  y: number;
  width: number;
  height: number;
  confidence: number;
};

type PidAnalysisSummary = {
  tagCount: number;
  equipmentCount: number;
  instrumentCount: number;
  lineNumberCount: number;
};

type DocumentItem = {
  documentId: string;
  projectName: string;
  originalFileName: string;
  contentType: string;
  uploadedByUserId: string;
  status: DocumentStatusWire;
  blobPath: string;
  hotspotCount: number;
  failureReason?: string | null;
  uploadedAtUtc: string;
  lastUpdatedAtUtc: string;
  hotspots: Hotspot[];
  analysis?: {
    schemaVersion: number;
    processorVersion: string;
    summary: PidAnalysisSummary;
  } | null;
};

type UploadResponse = {
  documents: Array<{
    documentId: string;
    projectName: string;
    originalFileName: string;
    status: DocumentStatusWire;
    blobPath: string;
    uploadedAtUtc: string;
  }>;
};

type AuthSession = {
  accessToken: string;
  tokenType: string;
  expiresAtUtc: string;
  userId: string;
  username: string;
  displayName: string;
  roles: string[];
};

const apiBaseUrl = import.meta.env.VITE_DOCUMENT_API_BASE_URL ?? "http://localhost:5000";
const authStorageKey = "aize.pid.auth";
const statusMap: Record<number, DocumentStatus> = {
  0: "Pending",
  1: "Processing",
  2: "Completed",
  3: "Failed"
};

function readStoredSession(): AuthSession | null {
  const raw = window.localStorage.getItem(authStorageKey);
  if (!raw) {
    return null;
  }

  try {
    return JSON.parse(raw) as AuthSession;
  } catch {
    return null;
  }
}

function normalizeStatus(status: DocumentStatusWire): DocumentStatus {
  return typeof status === "number" ? (statusMap[status] ?? "Pending") : status;
}

function normalizeDocument(document: DocumentItem): DocumentItem & { status: DocumentStatus } {
  return {
    ...document,
    status: normalizeStatus(document.status)
  };
}

function App() {
  const [projectName, setProjectName] = useState("North Sea Compression");
  const [username, setUsername] = useState("demo.operator");
  const [password, setPassword] = useState("Pass123!");
  const [authSession, setAuthSession] = useState<AuthSession | null>(() => readStoredSession());
  const [selectedFiles, setSelectedFiles] = useState<FileList | null>(null);
  const [documents, setDocuments] = useState<DocumentItem[]>([]);
  const [isUploading, setIsUploading] = useState(false);
  const [isAuthenticating, setIsAuthenticating] = useState(false);
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
    if (!authSession) {
      setDocuments([]);
      return;
    }

    window.localStorage.setItem(authStorageKey, JSON.stringify(authSession));
    void refreshDocuments(authSession.accessToken);
  }, [authSession]);

  useEffect(() => {
    if (!authSession) {
      return;
    }

    const connection = new signalR.HubConnectionBuilder()
      .withUrl(`${apiBaseUrl}/hubs/documents`, {
        accessTokenFactory: () => authSession.accessToken
      })
      .withAutomaticReconnect()
      .build();

    connection.on("DocumentUpdated", () => {
      void refreshDocuments(authSession.accessToken);
      setFeedMessage("Realtime status received from SignalR");
    });

    void connection.start().catch(() => {
      setFeedMessage("SignalR not connected yet");
    });

    return () => {
      void connection.stop();
    };
  }, [authSession]);

  async function authorizedFetch(url: string, init?: RequestInit) {
    if (!authSession) {
      throw new Error("No active session");
    }

    const headers = new Headers(init?.headers);
    headers.set("Authorization", `Bearer ${authSession.accessToken}`);
    const response = await fetch(url, { ...init, headers });

    if (response.status === 401) {
      handleLogout();
      setFeedMessage("Session expired. Please sign in again.");
    }

    return response;
  }

  async function refreshDocuments(accessToken: string) {
    const response = await fetch(`${apiBaseUrl}/api/documents`, {
      headers: {
        Authorization: `Bearer ${accessToken}`
      }
    });

    if (response.status === 401) {
      handleLogout();
      setFeedMessage("Session expired. Please sign in again.");
      return;
    }

    if (!response.ok) {
      setFeedMessage("Could not refresh document list");
      return;
    }

    const payload = (await response.json()) as DocumentItem[];
    setDocuments(payload.map(normalizeDocument));
  }

  async function handleLogin(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setIsAuthenticating(true);
    setFeedMessage("Signing in");

    const response = await fetch(`${apiBaseUrl}/api/auth/login`, {
      method: "POST",
      headers: {
        "Content-Type": "application/json"
      },
      body: JSON.stringify({ username, password })
    });

    setIsAuthenticating(false);

    if (!response.ok) {
      setFeedMessage("Login failed");
      return;
    }

    const payload = (await response.json()) as AuthSession;
    setAuthSession(payload);
    setFeedMessage(`Signed in as ${payload.displayName}`);
  }

  function handleLogout() {
    window.localStorage.removeItem(authStorageKey);
    setAuthSession(null);
    setDocuments([]);
  }

  async function handleUpload(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!selectedFiles?.length) {
      setFeedMessage("Pick at least one drawing first");
      return;
    }

    const formData = new FormData();
    formData.set("projectName", projectName);

    Array.from(selectedFiles).forEach((file) => {
      formData.append("files", file);
    });

    setIsUploading(true);
    setFeedMessage(`Dispatching ${selectedFiles.length} drawing(s) to ingestion`);

    const response = await authorizedFetch(`${apiBaseUrl}/api/documents/uploads`, {
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
    await refreshDocuments(authSession!.accessToken);
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
        <p className="eyebrow"> Organize messy documents</p>
        <h1>Upload hundreds of P&amp;ID drawings without pinning the UI.</h1>
        <p className="lede">
          The .NET ingestion service accepts the files, returns 202 immediately, and the Python processor
          resolves hotspots asynchronously through the realtime status stream.
        </p>
      </section>

      <section className="panel auth-panel">
        {!authSession ? (
          <form className="auth-form" onSubmit={handleLogin}>
            <div className="panel-heading">
              <h2>Authentication</h2>
              <p>Use a local bearer token issued by the .NET API.</p>
            </div>

            <label>
              Username
              <input value={username} onChange={(event) => setUsername(event.target.value)} />
            </label>

            <label>
              Password
              <input type="password" value={password} onChange={(event) => setPassword(event.target.value)} />
            </label>

            <button disabled={isAuthenticating} type="submit">
              {isAuthenticating ? "Signing in..." : "Sign in"}
            </button>

            <p className="meta-line">Demo accounts: demo.operator / Pass123! and demo.admin / Admin123!</p>
          </form>
        ) : (
          <div className="auth-summary">
            <div>
              <h2>{authSession.displayName}</h2>
              <p className="meta-line">{authSession.username}</p>
              <p className="meta-line">Roles: {authSession.roles.join(", ")}</p>
            </div>
            <button type="button" onClick={handleLogout}>
              Sign out
            </button>
          </div>
        )}
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

          <label className="drop-zone">
            <input
              type="file"
              multiple
              accept=".pdf,.png,.jpg,.jpeg"
              onChange={(event) => setSelectedFiles(event.target.files)}
            />
            <span>{selectedFiles?.length ? `${selectedFiles.length} files selected` : "Drop drawings or browse files"}</span>
          </label>

          <button disabled={isUploading || !authSession} type="submit">
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
              <article
                className={`document-card status-${normalizeStatus(document.status).toLowerCase()}`}
                key={document.documentId}
              >
                <div>
                  <p className="file-name">{document.originalFileName}</p>
                  <p className="meta-line">{document.projectName}</p>
                </div>
                <div className="status-badge">{normalizeStatus(document.status)}</div>
                <p className="meta-line">{new Date(document.lastUpdatedAtUtc).toLocaleString()}</p>
                <p className="meta-line">Overlays: {document.hotspotCount}</p>
                {document.analysis ? (
                  <p className="meta-line">
                    Tags: {document.analysis.summary.tagCount} · Equipment: {document.analysis.summary.equipmentCount}
                  </p>
                ) : null}
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
