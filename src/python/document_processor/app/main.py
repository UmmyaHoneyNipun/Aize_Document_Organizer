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


app = FastAPI(
    title="Aize Python Processor",
    description="Simulated OCR/CV worker for uploaded P&ID drawings.",
    version="0.1.0",
)


def _infer_labels(file_name: str, project_name: str) -> list[str]:
    matches = re.findall(r"[A-Z]{1,6}(?:[- ]\d{2,4})", file_name.upper())
    if matches:
        return matches[:8]

    project_token = re.sub(r"[^A-Z0-9]", "", project_name.upper())[:3] or "PID"
    return [
        f"{project_token}-101",
        "FIT 003",
        "PT 005",
        "XV 008",
        "P-207",
        "T-411",
        "HX-211",
        "65-PUW2-S6-164-IH",
    ]


def _normalize_text(raw_text: str) -> str:
    compact = re.sub(r"\s+", "-", raw_text.strip().upper())
    compact = re.sub(r"-{2,}", "-", compact)
    return compact


def _infer_kind_and_subtype(raw_text: str) -> tuple[str, str | None]:
    normalized = _normalize_text(raw_text)

    if re.fullmatch(r"\d{2,3}-[A-Z0-9]+(?:-[A-Z0-9]+){2,}", normalized):
        return "line_number", None

    equipment_prefixes = ("T-", "P-", "HX-", "TK-", "V-", "E-")
    if normalized.startswith(equipment_prefixes):
        subtype = normalized.split("-", 1)[0]
        return "equipment", subtype

    match = re.match(r"([A-Z]{1,4})-\d{2,4}$", normalized)
    if match:
        return "instrument_tag", match.group(1)

    return "annotation", None


def _rect_to_polygon(x: float, y: float, width: float, height: float) -> list[dict[str, float]]:
    return [
        {"x": x, "y": y},
        {"x": x + width, "y": y},
        {"x": x + width, "y": y + height},
        {"x": x, "y": y + height},
    ]


def _simulate_analysis(message: DocumentUploadedMessage) -> dict[str, Any]:
    seed = int(hashlib.sha256(message.document_id.encode("utf-8")).hexdigest()[:8], 16)
    rng = random.Random(seed)
    page_width = 1600
    page_height = 1200
    elements: list[dict[str, Any]] = []

    for index, raw_text in enumerate(_infer_labels(message.original_file_name, message.project_name), start=1):
        x = round(rng.uniform(40, page_width - 240), 2)
        y = round(rng.uniform(40, page_height - 120), 2)
        width = round(rng.uniform(90, 220), 2)
        height = round(rng.uniform(24, 72), 2)
        normalized_text = _normalize_text(raw_text)
        kind, subtype = _infer_kind_and_subtype(raw_text)

        elements.append(
            {
                "elementId": f"el_{index:03d}",
                "kind": kind,
                "subtype": subtype,
                "rawText": raw_text,
                "normalizedText": normalized_text,
                "bboxPx": {
                    "x": x,
                    "y": y,
                    "width": width,
                    "height": height,
                },
                "bboxNorm": {
                    "x": round(x / page_width, 6),
                    "y": round(y / page_height, 6),
                    "width": round(width / page_width, 6),
                    "height": round(height / page_height, 6),
                },
                "polygon": _rect_to_polygon(x, y, width, height),
                "confidence": {
                    "ocr": round(rng.uniform(0.86, 0.99), 2),
                    "detection": round(rng.uniform(0.82, 0.98), 2),
                    "overall": round(rng.uniform(0.84, 0.99), 2),
                },
                "attributes": [
                    {"name": "source", "value": "simulated"},
                    {"name": "pageNumber", "value": "1"},
                ],
                "relations": [],
            }
        )

    equipment_count = sum(1 for element in elements if element["kind"] == "equipment")
    instrument_count = sum(1 for element in elements if element["kind"] == "instrument_tag")
    line_number_count = sum(1 for element in elements if element["kind"] == "line_number")

    return {
        "schemaVersion": 2,
        "processorVersion": "pid-worker-0.3.0",
        "source": {
            "blobPath": message.blob_path,
            "contentType": message.content_type,
            "pageCount": 1,
            "imageWidth": page_width,
            "imageHeight": page_height,
        },
        "summary": {
            "tagCount": len(elements),
            "equipmentCount": equipment_count,
            "instrumentCount": instrument_count,
            "lineNumberCount": line_number_count,
        },
        "pages": [
            {
                "pageNumber": 1,
                "imageWidth": page_width,
                "imageHeight": page_height,
                "elements": elements,
            }
        ],
    }


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

    analysis = _simulate_analysis(message)
    element_count = sum(len(page["elements"]) for page in analysis["pages"])
    _post_json(
        f"{callback_base_url}/api/processing/completed",
        {
            "documentId": message.document_id,
            "analysis": analysis,
        },
    )

    return {
        "documentId": message.document_id,
        "processedAtUtc": datetime.now(timezone.utc).isoformat(),
        "hotspotCount": element_count,
        "status": "completed",
    }
