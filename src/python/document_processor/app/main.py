import hashlib
import json
import os
import random
import re
import urllib.request
from datetime import datetime, timezone
from typing import Any

from fastapi import FastAPI, HTTPException
from pydantic import BaseModel, Field


class DocumentUploadedMessage(BaseModel):
    document_id: str = Field(alias="documentId")
    project_name: str = Field(alias="projectName")
    original_file_name: str = Field(alias="originalFileName")
    content_type: str = Field(alias="contentType")
    uploaded_by_user_id: str = Field(alias="uploadedByUserId")
    blob_path: str = Field(alias="blobPath")
    uploaded_at_utc: datetime = Field(alias="uploadedAtUtc")
    schema_version: int = Field(alias="schemaVersion")
    correlation_id: str = Field(alias="correlationId")


class Hotspot(BaseModel):
    tag_number: str = Field(alias="tagNumber")
    x: float
    y: float
    width: float
    height: float
    confidence: float


app = FastAPI(
    title="Aize Python Processor",
    description="Simulated OCR/CV worker for uploaded P&ID drawings.",
    version="0.1.0",
)


def _infer_tags(file_name: str, project_name: str) -> list[str]:
    matches = re.findall(r"[A-Z]{1,4}-\d{2,4}", file_name.upper())
    if matches:
        return matches[:8]

    project_token = re.sub(r"[^A-Z0-9]", "", project_name.upper())[:3] or "PID"
    return [
        f"{project_token}-101",
        f"{project_token}-202",
        f"{project_token}-P01",
        f"{project_token}-V12",
    ]


def _simulate_hotspots(message: DocumentUploadedMessage) -> list[dict[str, Any]]:
    seed = int(hashlib.sha256(message.document_id.encode("utf-8")).hexdigest()[:8], 16)
    rng = random.Random(seed)
    hotspots: list[dict[str, Any]] = []

    for tag in _infer_tags(message.original_file_name, message.project_name):
        hotspots.append(
            {
                "tagNumber": tag,
                "x": round(rng.uniform(40, 1200), 2),
                "y": round(rng.uniform(40, 1500), 2),
                "width": round(rng.uniform(90, 180), 2),
                "height": round(rng.uniform(28, 72), 2),
                "confidence": round(rng.uniform(0.83, 0.99), 2),
            }
        )

    return hotspots


def _post_json(url: str, payload: dict[str, Any]) -> None:
    data = json.dumps(payload).encode("utf-8")
    request = urllib.request.Request(
        url,
        data=data,
        headers={"Content-Type": "application/json"},
        method="POST",
    )

    with urllib.request.urlopen(request, timeout=30) as response:
        if response.status >= 400:
            raise HTTPException(status_code=response.status, detail="Callback to document service failed.")


@app.get("/health")
def health() -> dict[str, str]:
    return {"status": "healthy"}


@app.post("/process")
def process_document(message: DocumentUploadedMessage) -> dict[str, Any]:
    callback_base_url = os.getenv("DOCUMENT_API_BASE_URL", "http://localhost:5000").rstrip("/")

    if not os.path.exists(message.blob_path):
        _post_json(
            f"{callback_base_url}/api/processing/failed",
            {
                "documentId": message.document_id,
                "reason": f"Uploaded blob was not found at {message.blob_path}",
            },
        )
        raise HTTPException(status_code=404, detail="Uploaded blob was not found.")

    hotspots = _simulate_hotspots(message)
    _post_json(
        f"{callback_base_url}/api/processing/completed",
        {
            "documentId": message.document_id,
            "hotspots": hotspots,
        },
    )

    return {
        "documentId": message.document_id,
        "processedAtUtc": datetime.now(timezone.utc).isoformat(),
        "hotspotCount": len(hotspots),
        "status": "completed",
    }
