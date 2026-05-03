# DocumentUploaded Contract

The event emitted by the .NET orchestration service is intentionally simple and blob-first.

```json
{
  "documentId": "c6582a29-c6c0-4497-bf42-d9506ccd0f2e",
  "projectName": "North Sea Compression",
  "originalFileName": "PID-AREA-A-101.pdf",
  "contentType": "application/pdf",
  "uploadedByUserId": "demo.user",
  "blobPath": "/shared/blob-storage/2026/04/23/c6582a29c6c04497bf42d9506ccd0f2e.pdf",
  "uploadedAtUtc": "2026-04-23T13:41:05.5213213+00:00",
  "schemaVersion": 1,
  "correlationId": "fe4c3b8c-3665-44d3-bf76-a57f3c27258b"
}
```

## Why this shape works

- The queue stays lean because the binary never rides inside the message.
- The consumer can re-fetch the blob if it crashes and restarts.
- `schemaVersion` gives you a clean path for contract evolution.
- `correlationId` lets you trace a drawing through API, queue, worker, and SignalR logs.

## Completion callback

The Python processor calls back to the .NET service with:

```json
{
  "documentId": "c6582a29-c6c0-4497-bf42-d9506ccd0f2e",
  "analysis": {
    "schemaVersion": 2,
    "processorVersion": "pid-worker-0.3.0",
    "source": {
      "blobPath": "/shared/blob-storage/2026/04/23/c6582a29c6c04497bf42d9506ccd0f2e.pdf",
      "contentType": "application/pdf",
      "pageCount": 1,
      "imageWidth": 1600,
      "imageHeight": 1200
    },
    "summary": {
      "tagCount": 8,
      "equipmentCount": 3,
      "instrumentCount": 4,
      "lineNumberCount": 1
    },
    "pages": [
      {
        "pageNumber": 1,
        "imageWidth": 1600,
        "imageHeight": 1200,
        "elements": [
          {
            "elementId": "el_001",
            "kind": "instrument_tag",
            "subtype": "FIT",
            "rawText": "FIT 003",
            "normalizedText": "FIT-003",
            "bboxPx": {
              "x": 328.4,
              "y": 706.11,
              "width": 116.3,
              "height": 41.5
            },
            "bboxNorm": {
              "x": 0.20525,
              "y": 0.588425,
              "width": 0.072687,
              "height": 0.034583
            },
            "polygon": [
              { "x": 328.4, "y": 706.11 },
              { "x": 444.7, "y": 706.11 },
              { "x": 444.7, "y": 747.61 },
              { "x": 328.4, "y": 747.61 }
            ],
            "confidence": {
              "ocr": 0.96,
              "detection": 0.92,
              "overall": 0.94
            },
            "attributes": [
              { "name": "source", "value": "simulated" }
            ],
            "relations": []
          }
        ]
      }
    ]
  }
}
```
