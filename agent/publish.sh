#!/usr/bin/env bash
# Publish pm-code-agent come eseguibile singolo autocontenuto per Linux x64 (es. Ubuntu)
# Uso: ./publish.sh
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$SCRIPT_DIR/pm-code"
OUT_DIR="$SCRIPT_DIR/dist"

echo "Build & Publish..."

dotnet publish "$PROJECT_DIR/pm-code.csproj" \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true \
    -o "$OUT_DIR"

chmod +x "$OUT_DIR/pm-code"

echo ""
echo "Distribuibile pronto in: $OUT_DIR"
echo ""
echo "File da copiare sulla macchina di destinazione:"
find "$OUT_DIR" -maxdepth 1 -type f \( -name "pm-code" -o -name "*.json" \) -exec ls -lh {} \;
