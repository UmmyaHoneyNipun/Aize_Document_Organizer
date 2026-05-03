import hashlib
import json
import os
import random
import re
import urllib.request
from dataclasses import dataclass
from datetime import datetime, timezone
from io import BytesIO
from pathlib import Path
from typing import Any

import fitz
import pytesseract
from fastapi import FastAPI, HTTPException
from PIL import Image, ImageOps
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


@dataclass(frozen=True)
class PidClassificationResult:
    is_pid: bool
    score: float
    threshold: float
    labels: list[str]
    instrument_count: int
    equipment_count: int
    line_number_count: int
    keyword_hits: int
    horizontal_line_peaks: int
    vertical_line_peaks: int
    dark_pixel_ratio: float
    embedded_text_length: int
    ocr_text_length: int
    reason: str


app = FastAPI(
    title="Aize Python Processor",
    description="OCR/CV worker with P&ID document classification and gated analysis.",
    version="0.2.0",
)

PID_INSTRUMENT_TAG_PATTERN = re.compile(
    r"\b(?:AIT|FAL|FA|FCV|FE|FI|FIC|FIT|FQ|FR|FS|FT|FY|LA|LAL|LIC|LIR|LIT|LS|LT|PCV|PI|PIC|PIT|PRV|PSV|PT|SG|SCS|ST|TCV|TI|TIT|TT|V|XV|ZS)[- ]?\d{2,4}[A-Z]?\b"
)
PID_EQUIPMENT_TAG_PATTERN = re.compile(r"\b(?:T|TK|P|HX|V|E|C|FL)-\d{2,4}[A-Z]?\b")
PID_LINE_NUMBER_PATTERN = re.compile(r"\b\d{2,3}-[A-Z0-9]+(?:-[A-Z0-9]+){2,}\b")
PID_KEYWORD_PATTERN = re.compile(
    r"\b(?:P&ID|PIPING|INSTRUMENTATION|PROCESS|DRAIN|VENT|TANK|PUMP|VALVE|STEAM|CONDENSATE|SETPOINT|NOZZLE|FLG|OCR|HOTSPOT)\b"
)
PROSE_WORD_PATTERN = re.compile(r"\b[A-Z]{5,}\b")


def _normalize_text(raw_text: str) -> str:
    compact = re.sub(r"\s+", "-", raw_text.strip().upper())
    compact = re.sub(r"-{2,}", "-", compact)
    return compact


def _canonicalize_label(raw_text: str) -> str:
    compact = raw_text.strip().upper()
    compact = re.sub(r"\s+", " ", compact)
    return compact.replace(" - ", "-")


def _infer_kind_and_subtype(raw_text: str) -> tuple[str, str | None]:
    normalized = _normalize_text(raw_text)

    if PID_LINE_NUMBER_PATTERN.fullmatch(normalized):
        return "line_number", None

    equipment_prefixes = ("T-", "P-", "HX-", "TK-", "V-", "E-", "C-", "FL-")
    if normalized.startswith(equipment_prefixes):
        subtype = normalized.split("-", 1)[0]
        return "equipment", subtype

    match = re.match(r"([A-Z]{1,4})-\d{2,4}[A-Z]?$", normalized)
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


def _is_pdf(message: DocumentUploadedMessage) -> bool:
    return (
        message.content_type.lower() == "application/pdf"
        or Path(message.original_file_name).suffix.lower() == ".pdf"
        or Path(message.blob_path).suffix.lower() == ".pdf"
    )


def _load_document_preview(message: DocumentUploadedMessage) -> tuple[Image.Image, str]:
    if _is_pdf(message):
        with fitz.open(message.blob_path) as document:
            if document.page_count == 0:
                raise ValueError("The uploaded PDF does not contain any pages.")

            page = document.load_page(0)
            embedded_text = page.get_text("text")
            pixmap = page.get_pixmap(matrix=fitz.Matrix(2, 2), alpha=False)
            preview = Image.open(BytesIO(pixmap.tobytes("png"))).convert("RGB")
            return preview, embedded_text

    with Image.open(message.blob_path) as source_image:
        preview = ImageOps.exif_transpose(source_image).convert("RGB")
        return preview, ""


def _extract_sparse_text(image: Image.Image) -> str:
    grayscale = ImageOps.autocontrast(ImageOps.grayscale(image))
    width, height = grayscale.size
    longest_edge = max(width, height)

    if longest_edge < 1800:
        scale = 1800 / longest_edge
        resized = grayscale.resize(
            (max(1, int(width * scale)), max(1, int(height * scale))),
            Image.Resampling.LANCZOS,
        )
    else:
        resized = grayscale

    return pytesseract.image_to_string(resized, config="--oem 3 --psm 11")


def _extract_pid_labels(text: str) -> list[str]:
    labels: list[str] = []
    seen: set[str] = set()

    for pattern in (PID_LINE_NUMBER_PATTERN, PID_EQUIPMENT_TAG_PATTERN, PID_INSTRUMENT_TAG_PATTERN):
        for match in pattern.finditer(text):
            label = _canonicalize_label(match.group(0))
            if label in seen:
                continue

            seen.add(label)
            labels.append(label)

    return labels


def _count_projection_peaks(values: list[int], threshold: int) -> int:
    peaks = 0
    run_length = 0

    for value in values:
        if value >= threshold:
            run_length += 1
            continue

        if run_length >= 2:
            peaks += 1
        run_length = 0

    if run_length >= 2:
        peaks += 1

    return peaks


def _measure_line_structure(image: Image.Image) -> tuple[float, int, int]:
    preview = ImageOps.autocontrast(ImageOps.grayscale(image))
    preview.thumbnail((512, 512))

    width, height = preview.size
    pixels = preview.load()
    row_dark_counts = [0 for _ in range(height)]
    column_dark_counts = [0 for _ in range(width)]
    dark_pixel_total = 0

    for y in range(height):
        for x in range(width):
            if pixels[x, y] < 176:
                dark_pixel_total += 1
                row_dark_counts[y] += 1
                column_dark_counts[x] += 1

    total_pixels = max(1, width * height)
    dark_pixel_ratio = dark_pixel_total / total_pixels
    horizontal_peaks = _count_projection_peaks(row_dark_counts, max(18, int(width * 0.38)))
    vertical_peaks = _count_projection_peaks(column_dark_counts, max(18, int(height * 0.2)))

    return dark_pixel_ratio, horizontal_peaks, vertical_peaks


def _classify_pid_document(message: DocumentUploadedMessage) -> PidClassificationResult:
    preview, embedded_text = _load_document_preview(message)
    ocr_text = _extract_sparse_text(preview)

    combined_text = "\n".join(
        part for part in (embedded_text, ocr_text, message.original_file_name, message.project_name) if part
    ).upper()
    combined_text = re.sub(r"[_|]+", " ", combined_text)
    labels = _extract_pid_labels(combined_text)
    dark_pixel_ratio, horizontal_line_peaks, vertical_line_peaks = _measure_line_structure(preview)

    instrument_count = sum(1 for label in labels if PID_INSTRUMENT_TAG_PATTERN.fullmatch(label))
    equipment_count = sum(1 for label in labels if PID_EQUIPMENT_TAG_PATTERN.fullmatch(label))
    line_number_count = sum(1 for label in labels if PID_LINE_NUMBER_PATTERN.fullmatch(label))
    keyword_hits = len(set(PID_KEYWORD_PATTERN.findall(combined_text)))
    prose_word_count = len(PROSE_WORD_PATTERN.findall(combined_text))
    structural_label_count = instrument_count + equipment_count + line_number_count

    line_structure_score = 0.0
    if 0.01 <= dark_pixel_ratio <= 0.35:
        line_structure_score = min(horizontal_line_peaks + vertical_line_peaks, 8) * 0.7

    score = (
        (instrument_count * 2.0)
        + (equipment_count * 2.6)
        + (line_number_count * 4.0)
        + (keyword_hits * 0.75)
        + line_structure_score
    )

    if prose_word_count > 18 and structural_label_count <= 1:
        score -= 3.5
    elif prose_word_count > 12 and line_number_count == 0 and equipment_count == 0:
        score -= 2.0

    threshold = float(os.getenv("PID_CLASSIFICATION_THRESHOLD", "8.5"))
    is_pid = (
        score >= threshold
        and structural_label_count >= 3
        and (instrument_count >= 2 or equipment_count >= 1 or line_number_count >= 1)
    )

    reason = (
        f"P&ID score {score:.1f}/{threshold:.1f}; "
        f"instrument tags={instrument_count}, equipment tags={equipment_count}, "
        f"line numbers={line_number_count}, keywords={keyword_hits}, "
        f"line peaks={horizontal_line_peaks + vertical_line_peaks}"
    )

    if not is_pid:
        reason = (
            "Uploaded document does not look like a P&ID drawing. "
            f"{reason}. The processor refused to persist extracted analysis."
        )

    return PidClassificationResult(
        is_pid=is_pid,
        score=round(score, 2),
        threshold=threshold,
        labels=labels[:16],
        instrument_count=instrument_count,
        equipment_count=equipment_count,
        line_number_count=line_number_count,
        keyword_hits=keyword_hits,
        horizontal_line_peaks=horizontal_line_peaks,
        vertical_line_peaks=vertical_line_peaks,
        dark_pixel_ratio=round(dark_pixel_ratio, 4),
        embedded_text_length=len(embedded_text.strip()),
        ocr_text_length=len(ocr_text.strip()),
        reason=reason,
    )


def _build_analysis(message: DocumentUploadedMessage, labels: list[str], classification: PidClassificationResult) -> dict[str, Any]:
    seed = int(hashlib.sha256(message.document_id.encode("utf-8")).hexdigest()[:8], 16)
    rng = random.Random(seed)
    page_width = 1600
    page_height = 1200
    elements: list[dict[str, Any]] = []

    for index, raw_text in enumerate(labels, start=1):
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
                    "ocr": round(rng.uniform(0.84, 0.99), 2),
                    "detection": round(rng.uniform(0.8, 0.98), 2),
                    "overall": round(rng.uniform(0.83, 0.99), 2),
                },
                "attributes": [
                    {"name": "source", "value": "ocr-seeded-simulation"},
                    {"name": "classifierScore", "value": str(classification.score)},
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
        "processorVersion": "pid-worker-0.4.0",
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

    try:
        classification = _classify_pid_document(message)
    except Exception as exc:  # pragma: no cover - defensive failure callback path
        reason = f"Document inspection failed before OCR classification: {exc}"
        _post_json(
            f"{callback_base_url}/api/processing/failed",
            {
                "documentId": message.document_id,
                "reason": reason,
            },
        )
        raise HTTPException(status_code=422, detail=reason) from exc

    if not classification.is_pid:
        _post_json(
            f"{callback_base_url}/api/processing/failed",
            {
                "documentId": message.document_id,
                "reason": classification.reason,
            },
        )
        raise HTTPException(status_code=422, detail=classification.reason)

    analysis = _build_analysis(message, classification.labels, classification)
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
        "classificationScore": classification.score,
        "status": "completed",
    }
