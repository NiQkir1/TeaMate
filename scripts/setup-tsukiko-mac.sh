#!/usr/bin/env bash
# =============================================================================
# Fotoprint AI Gateway - One-Click Tsukiko Local STT Setup (macOS)
# =============================================================================
# Automatically verifies or installs Tsukiko (whisper.cpp Metal CLI) and
# downloads the Whisper GGML model (ggml-large-v3-turbo.bin or ggml-base.bin)
# without requiring root/sudo privileges.
#
# Usage:
#   bash scripts/setup-tsukiko-mac.sh [--light] [--skip-model]
# =============================================================================

set -euo pipefail

LIGHT_MODEL=false
SKIP_MODEL=false

print_usage() {
    echo "Usage: $0 [--light] [--skip-model]"
    echo "  --light       Download lightweight ggml-base.bin (~148 MB) instead of ggml-large-v3-turbo.bin (~1.6 GB)"
    echo "  --skip-model  Skip downloading the Whisper GGML model if missing"
}

for arg in "$@"; do
    case "$arg" in
        --light|-l)
            LIGHT_MODEL=true
            ;;
        --skip-model|-s)
            SKIP_MODEL=true
            ;;
        --help|-h)
            print_usage
            exit 0
            ;;
        *)
            echo "[ERROR] Unknown argument: $arg" >&2
            print_usage >&2
            exit 1
            ;;
    esac
done

GREEN='\033[0;32m'
CYAN='\033[0;36m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
GRAY='\033[0;90m'
NC='\033[0m'

log_info()  { printf "${CYAN}[INFO]${NC} %s\n" "$1"; }
log_debug() { printf "${GRAY}[DEBUG]${NC} %s\n" "$1"; }
log_ok()    { printf "${GREEN}[OK]${NC} %s\n" "$1"; }
log_warn()  { printf "${YELLOW}[WARN]${NC} %s\n" "$1"; }
log_err()   { printf "${RED}[ERROR]${NC} %s\n" "$1" >&2; }

echo -e "${CYAN}=================================================================${NC}"
echo -e "${CYAN} Fotoprint AI Gateway - Tsukiko Local STT Setup (macOS)          ${NC}"
echo -e "${CYAN}=================================================================${NC}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
TOOLS_DIR="$REPO_ROOT/tools/tsukiko"

log_debug "Repository root: $REPO_ROOT"
log_debug "Portable tools directory: $TOOLS_DIR"

# 1. Check existing tsukiko-transcribe binary
CANDIDATE_BINS=(
    "/Applications/tsukiko.app/Contents/Helpers/tsukiko-transcribe"
    "$HOME/Applications/tsukiko.app/Contents/Helpers/tsukiko-transcribe"
    "$TOOLS_DIR/tsukiko.app/Contents/Helpers/tsukiko-transcribe"
    "$TOOLS_DIR/tsukiko-transcribe"
)

TSUKIKO_BIN=""
for candidate in "${CANDIDATE_BINS[@]}"; do
    if [[ -x "$candidate" ]]; then
        TSUKIKO_BIN="$candidate"
        break
    fi
done

# 2. Download & install Tsukiko if not found
if [[ -n "$TSUKIKO_BIN" ]]; then
    log_ok "Found existing Tsukiko CLI: $TSUKIKO_BIN"
else
    log_info "Tsukiko CLI not found. Downloading Tsukiko v1.5.3 for macOS..."
    DMG_URL="https://github.com/Yukovsky/tsukiko/releases/download/v1.5.3/tsukiko.dmg"
    TMP_DMG="/tmp/tsukiko.dmg"
    MOUNT_DIR=""

    cleanup_dmg() {
        if [[ -n "${MOUNT_DIR:-}" && -d "${MOUNT_DIR:-}" ]]; then
            hdiutil detach "$MOUNT_DIR" -quiet 2>/dev/null || true
        fi
        rm -f "$TMP_DMG"
    }
    trap cleanup_dmg EXIT

    mkdir -p "$TOOLS_DIR"

    if ! curl -fL --progress-bar -o "$TMP_DMG" "$DMG_URL"; then
        log_err "Failed to download Tsukiko DMG from $DMG_URL"
        exit 1
    fi

    log_info "Mounting $TMP_DMG via hdiutil..."
    MOUNT_OUTPUT="$(hdiutil attach "$TMP_DMG" -nobrowse)"
    MOUNT_DIR="$(echo "$MOUNT_OUTPUT" | grep -o '/Volumes/.*' | head -n 1 || true)"

    if [[ -z "$MOUNT_DIR" || ! -d "$MOUNT_DIR/tsukiko.app" ]]; then
        log_err "Could not locate tsukiko.app inside mounted DMG (mount point: ${MOUNT_DIR:-none})"
        exit 1
    fi

    log_info "Copying tsukiko.app to $TOOLS_DIR/tsukiko.app ..."
    rm -rf "$TOOLS_DIR/tsukiko.app"
    cp -R "$MOUNT_DIR/tsukiko.app" "$TOOLS_DIR/tsukiko.app"

    # Also copy to ~/Applications/tsukiko.app if possible (user-level, no sudo needed)
    mkdir -p "$HOME/Applications" 2>/dev/null || true
    if [[ -w "$HOME/Applications" && ! -d "$HOME/Applications/tsukiko.app" ]]; then
        cp -R "$MOUNT_DIR/tsukiko.app" "$HOME/Applications/tsukiko.app" 2>/dev/null || true
    fi

    hdiutil detach "$MOUNT_DIR" -quiet 2>/dev/null || true
    MOUNT_DIR=""
    rm -f "$TMP_DMG"

    # Remove quarantine attribute if present so macOS Gatekeeper does not block CLI execution
    xattr -dr com.apple.quarantine "$TOOLS_DIR/tsukiko.app" 2>/dev/null || true

    ln -sf "$TOOLS_DIR/tsukiko.app/Contents/Helpers/tsukiko-transcribe" "$TOOLS_DIR/tsukiko-transcribe"
    TSUKIKO_BIN="$TOOLS_DIR/tsukiko.app/Contents/Helpers/tsukiko-transcribe"

    if [[ ! -x "$TSUKIKO_BIN" ]]; then
        chmod +x "$TSUKIKO_BIN" 2>/dev/null || true
    fi

    if [[ ! -x "$TSUKIKO_BIN" ]]; then
        log_err "Installed tsukiko-transcribe is not executable at $TSUKIKO_BIN"
        exit 1
    fi

    log_ok "Installed Tsukiko CLI at: $TSUKIKO_BIN"
fi

# 3. Check for Whisper GGML model
if [[ "$LIGHT_MODEL" == "true" ]]; then
    TARGET_MODEL="ggml-base.bin"
    MODEL_CHECK_LIST=("ggml-base.bin" "ggml-large-v3-turbo.bin")
else
    TARGET_MODEL="ggml-large-v3-turbo.bin"
    MODEL_CHECK_LIST=("ggml-large-v3-turbo.bin" "ggml-base.bin")
fi

MAC_MODELS_DIR="$HOME/Library/Application Support/app.yuko.tsukiko/models"
PORTABLE_MODELS_DIR="$TOOLS_DIR/models"

MODEL_SEARCH_DIRS=(
    "$MAC_MODELS_DIR"
    "$PORTABLE_MODELS_DIR"
)

FOUND_MODEL=""
for dir in "${MODEL_SEARCH_DIRS[@]}"; do
    for model_name in "${MODEL_CHECK_LIST[@]}"; do
        candidate_model="$dir/$model_name"
        if [[ -f "$candidate_model" ]]; then
            file_size=$(stat -f%z "$candidate_model" 2>/dev/null || stat -c%s "$candidate_model" 2>/dev/null || echo 0)
            if [[ "$file_size" -gt 1048576 ]]; then
                FOUND_MODEL="$candidate_model"
                break
            fi
        fi
    done
    if [[ -n "$FOUND_MODEL" ]]; then
        break
    fi
done

if [[ -n "$FOUND_MODEL" ]]; then
    log_ok "Found local Whisper model: $FOUND_MODEL"
elif [[ "$SKIP_MODEL" == "true" ]]; then
    log_warn "Whisper model not found, but --skip-model was specified. Skipping download."
else
    mkdir -p "$MAC_MODELS_DIR"
    mkdir -p "$PORTABLE_MODELS_DIR"

    MODEL_URL="https://huggingface.co/ggerganov/whisper.cpp/resolve/main/$TARGET_MODEL"
    DEST_MODEL="$MAC_MODELS_DIR/$TARGET_MODEL"

    log_info "Whisper model not found locally. Downloading $TARGET_MODEL from HuggingFace..."
    log_info "Source URL: $MODEL_URL"
    log_info "Destination: $DEST_MODEL"

    if ! curl -fL --progress-bar -o "$DEST_MODEL" "$MODEL_URL"; then
        log_err "Failed to download Whisper model '$TARGET_MODEL' from $MODEL_URL"
        rm -f "$DEST_MODEL"
        exit 1
    fi

    FOUND_MODEL="$DEST_MODEL"
    log_ok "Downloaded Whisper model: $FOUND_MODEL"
fi

# 4. Run tsukiko-transcribe --status and output readiness summary
log_info "Verifying Tsukiko CLI status..."
STATUS_JSON="$("$TSUKIKO_BIN" --status)"
echo "$STATUS_JSON"
echo ""
log_ok "================================================================="
log_ok " [OK] Tsukiko Local Speech-to-Text (Metal GPU) is READY!         "
log_ok " CLI Binary : $TSUKIKO_BIN"
if [[ -n "$FOUND_MODEL" ]]; then
    log_ok " Whisper Mod: $FOUND_MODEL"
fi
log_ok "================================================================="
