#!/usr/bin/env bash
# ==============================================================================
# AriaUI Automated Release & Packaging Build Script
# Conforming to LIBARIA2_ARCHITECTURE_PLAN.md & LIBARIA2_TEST_PLAN.md Phase 4
# ==============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
cd "${REPO_ROOT}"

echo "================================================="
echo " AriaUI Release & Distribution Package Builder   "
echo "================================================="
echo "Repo root:      ${REPO_ROOT}"
echo "Date:           $(date -u +"%Y-%m-%d %H:%M:%SZ")"
echo "System:         $(uname -s) $(uname -r) ($(uname -m))"
echo

# 1. Verify Prerequisites
echo "[Step 1/6] Verifying toolchain prerequisites..."
command -v dotnet >/dev/null 2>&1 || { echo "ERROR: 'dotnet' command not found. .NET 10 SDK is required."; exit 1; }
command -v g++ >/dev/null 2>&1 || { echo "ERROR: 'g++' compiler not found."; exit 1; }
command -v make >/dev/null 2>&1 || { echo "ERROR: 'make' tool not found."; exit 1; }

DOTNET_VER=$(dotnet --version)
echo "  .NET SDK Version: ${DOTNET_VER}"
echo "  G++ Version:      $(g++ -dumpversion)"

# 2. Check and compile native libraries (libaria2.so & libaria2_bridge.so)
echo
echo "[Step 2/6] Preparing native shared libraries..."
RUNTIMES_DIR="${REPO_ROOT}/runtimes/linux-x64/native"
mkdir -p "${RUNTIMES_DIR}"

LIBARIA2_SOURCE_DIR="${LIBARIA2_SOURCE_DIR:-/tmp/aria2_build/aria2-1.37.0}"
if [[ ! -f "${LIBARIA2_SOURCE_DIR}/src/.libs/libaria2.so" ]]; then
    echo "  libaria2.so not found at ${LIBARIA2_SOURCE_DIR}/src/.libs/libaria2.so"
    echo "  Please compile upstream aria2 1.37.0 first or set LIBARIA2_SOURCE_DIR."
    exit 1
fi

echo "  Copying libaria2.so from ${LIBARIA2_SOURCE_DIR}..."
cp -f "${LIBARIA2_SOURCE_DIR}/src/.libs/libaria2.so"* "${RUNTIMES_DIR}/"

echo "  Compiling libaria2_bridge.so..."
g++ -shared -fPIC -O2 -std=c++14 \
    -I"${REPO_ROOT}/native/bridge" \
    -I"${LIBARIA2_SOURCE_DIR}/src/includes" \
    "${REPO_ROOT}/native/bridge/aria2_bridge.cpp" \
    -L"${LIBARIA2_SOURCE_DIR}/src/.libs" -laria2 \
    -Wl,-rpath,'$ORIGIN' -o "${RUNTIMES_DIR}/libaria2_bridge.so"

echo "  Native libraries ready in ${RUNTIMES_DIR}:"
ls -lh "${RUNTIMES_DIR}"

# 3. Build Debug & Release Binaries
echo
echo "[Step 3/6] Building Debug & Release configurations..."
dotnet build AriaUI.csproj -c Debug
dotnet build AriaUI.csproj -c Release

# 4a. Publish Self-Contained Debug Package
echo
echo "[Step 4a/7] Publishing Self-Contained Debug package..."
PUBLISH_DEBUG_DIR="${REPO_ROOT}/publish/linux-x64-debug"
dotnet publish AriaUI.csproj -c Debug -r linux-x64 --self-contained -o "${PUBLISH_DEBUG_DIR}"

# 4b. Publish Self-Contained Release Package
echo
echo "[Step 4b/7] Publishing Self-Contained Release package..."
PUBLISH_RELEASE_DIR="${REPO_ROOT}/publish/linux-x64-release"
dotnet publish AriaUI.csproj -c Release -r linux-x64 --self-contained -o "${PUBLISH_RELEASE_DIR}"

# 5. Publish Native AOT Package
echo
echo "[Step 5/7] Publishing Native AOT package..."
PUBLISH_AOT_DIR="${REPO_ROOT}/publish/linux-x64-aot"
dotnet publish AriaUI.csproj -c Release -r linux-x64 /p:PublishAot=true -o "${PUBLISH_AOT_DIR}"

# 6. Publish Thin Host Tool (AriaUI.Host)
echo
echo "[Step 6/7] Publishing AriaUI.Host thin host..."
PUBLISH_HOST_DIR="${REPO_ROOT}/publish/linux-x64-host"
dotnet publish tools/AriaUI.Host/AriaUI.Host.csproj -c Release -r linux-x64 --self-contained -o "${PUBLISH_HOST_DIR}"

# 7. Generate Checksums
echo
echo "================================================="
echo " Generating SHA256 Checksums                     "
echo "================================================="
CHECKSUMS_FILE="${REPO_ROOT}/packaging/SHA256SUMS"
rm -f "${CHECKSUMS_FILE}"

pushd "${REPO_ROOT}" >/dev/null
{
    echo "# AriaUI Release Artifacts SHA256 Checksums"
    echo "# Generated on $(date -u +"%Y-%m-%d %H:%M:%SZ")"
    echo
    echo "# --- Native Shared Libraries ---"
    sha256sum runtimes/linux-x64/native/libaria2.so
    sha256sum runtimes/linux-x64/native/libaria2_bridge.so
    echo
    echo "# --- Native AOT Published Artifacts ---"
    sha256sum publish/linux-x64-aot/AriaUI
    sha256sum publish/linux-x64-aot/libSkiaSharp.so
    sha256sum publish/linux-x64-aot/libHarfBuzzSharp.so
    echo
    echo "# --- Self-Contained Release Published Artifacts ---"
    sha256sum publish/linux-x64-release/AriaUI
    sha256sum publish/linux-x64-release/AriaUI.dll
    echo
    echo "# --- Self-Contained Debug Published Artifacts ---"
    sha256sum publish/linux-x64-debug/AriaUI
    sha256sum publish/linux-x64-debug/AriaUI.dll
    echo
    echo "# --- Thin Host (Native Messaging) Artifacts ---"
    sha256sum publish/linux-x64-host/AriaUI.Host
} > "${CHECKSUMS_FILE}"
popd >/dev/null

echo "Checksums written to: ${CHECKSUMS_FILE}"
cat "${CHECKSUMS_FILE}"
echo
echo "Build and packaging completed successfully."
