#!/bin/sh
set -eu

python -m uvicorn app.main:app --host 0.0.0.0 --port 8001 &
PYTHON_PID=$!

cleanup() {
  kill "$PYTHON_PID" 2>/dev/null || true
  wait "$PYTHON_PID" 2>/dev/null || true
}

trap cleanup INT TERM EXIT

dotnet Aize.DocumentService.Api.dll
