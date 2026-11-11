<#
.SYNOPSIS
    One-click cross-platform setup script for Tsukiko local speech-to-text on Windows (No Admin rights required).
.DESCRIPTION
    Checks for an existing installation of tsukiko-transcribe.exe and local Whisper GGML models.
    If missing, downloads and extracts Tsukiko Portable v1.5.3 into ./tools/tsukiko/ and downloads
    the Whisper model (ggml-large-v3-turbo.bin by default, or ggml-base.bin with -LightModel) from HuggingFace.
.PARAMETER LightModel
    Downloads the lightweight ggml-base.bin (~148 MB) instead of ggml-large-v3-turbo.bin (~1.6 GB).
.PARAMETER SkipModelDownload
    Skips downloading the Whisper GGML model file if not found locally.
#>
param(
    [switch]$LightModel = $false,
    [switch]$SkipModelDownload = $false
)

$ErrorActionPreference = "Stop"

function Write-Info($msg)  { Write-Host "[INFO] $msg" -ForegroundColor Cyan }
function Write-DebugLog($msg) { Write-Host "[DEBUG] $msg" -ForegroundColor DarkGray }
function Write-Ok($msg)    { Write-Host "[OK] $msg" -ForegroundColor Green }
function Write-Warn($msg)  { Write-Host "[WARN] $msg" -ForegroundColor Yellow }
function Write-Err($msg)   { Write-Host "[ERROR] $msg" -ForegroundColor Red }

Write-Host "=================================================================" -ForegroundColor Cyan
Write-Host " Fotoprint AI Gateway - Tsukiko Local STT Setup (Windows)        " -ForegroundColor Cyan
Write-Host "=================================================================" -ForegroundColor Cyan

# Determine repository root
$ScriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $ScriptDir) { $ScriptDir = (Get-Location).Path }
$RepoRoot = Split-Path -Parent $ScriptDir
if (-not $RepoRoot -or -not (Test-Path $RepoRoot)) { $RepoRoot = (Get-Location).Path }

$ToolsDir = Join-Path $RepoRoot "tools\tsukiko"
Write-DebugLog "Repository root: $RepoRoot"
Write-DebugLog "Portable tools directory: $ToolsDir"

# 1. Check existing tsukiko-transcribe.exe paths
$CandidateCliPaths = @(
    (Join-Path $ToolsDir "tsukiko-transcribe.exe"),
    (Join-Path $ToolsDir "Helpers\tsukiko-transcribe.exe"),
    (Join-Path $env:LOCALAPPDATA "Programs\tsukiko\tsukiko-transcribe.exe"),
    "C:\Program Files\tsukiko\tsukiko-transcribe.exe"
)

$TsukikoExe = $null
foreach ($candidate in $CandidateCliPaths) {
    if ($candidate -and (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        $TsukikoExe = $candidate
        break
    }
}

if (-not $TsukikoExe -and (Test-Path -LiteralPath $ToolsDir -PathType Container)) {
    $found = Get-ChildItem -Path $ToolsDir -Filter "tsukiko-transcribe.exe" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($found) {
        $TsukikoExe = $found.FullName
    }
}

# 2. Download & extract Tsukiko Portable if not found
if ($TsukikoExe) {
    Write-Ok "Found existing Tsukiko CLI: $TsukikoExe"
} else {
    Write-Info "Tsukiko CLI not found. Installing portable version into $ToolsDir (no admin rights required)..."
    try {
        if (-not (Test-Path -LiteralPath $ToolsDir)) {
            New-Item -ItemType Directory -Path $ToolsDir -Force | Out-Null
        }

        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        $ReleaseUrl = "https://github.com/Yukovsky/tsukiko/releases/download/v1.5.3/tsukiko-portable.zip"
        $ZipPath = Join-Path $ToolsDir "tsukiko-portable.zip"

        Write-Info "Downloading Tsukiko Portable v1.5.3 from $ReleaseUrl ..."
        Invoke-WebRequest -Uri $ReleaseUrl -OutFile $ZipPath -UseBasicParsing

        Write-Info "Extracting archive to $ToolsDir ..."
        Expand-Archive -Path $ZipPath -DestinationPath $ToolsDir -Force

        if (Test-Path -LiteralPath $ZipPath) {
            Remove-Item -LiteralPath $ZipPath -Force
            Write-DebugLog "Cleaned up temporary archive $ZipPath"
        }

        # Re-check extracted binary location
        foreach ($candidate in $CandidateCliPaths) {
            if ($candidate -and (Test-Path -LiteralPath $candidate -PathType Leaf)) {
                $TsukikoExe = $candidate
                break
            }
        }
        if (-not $TsukikoExe) {
            $found = Get-ChildItem -Path $ToolsDir -Filter "tsukiko-transcribe.exe" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($found) {
                $TsukikoExe = $found.FullName
            }
        }

        if (-not $TsukikoExe) {
            throw "tsukiko-transcribe.exe was not found inside the extracted archive at $ToolsDir."
        }

        Write-Ok "Tsukiko Portable extracted successfully: $TsukikoExe"
    } catch {
        Write-Err "Failed to download or extract Tsukiko Portable: $($_.Exception.Message)"
        exit 1
    }
}

# 3. Check for Whisper GGML model
$TargetModelName = if ($LightModel) { "ggml-base.bin" } else { "ggml-large-v3-turbo.bin" }
$ModelNamesToCheck = if ($LightModel) {
    @("ggml-base.bin", "ggml-large-v3-turbo.bin")
} else {
    @("ggml-large-v3-turbo.bin", "ggml-base.bin")
}

$ModelSearchDirs = @(
    (Join-Path $env:APPDATA "app.yuko.tsukiko\models"),
    (Join-Path $env:LOCALAPPDATA "app.yuko.tsukiko\models"),
    (Join-Path $ToolsDir "models")
)

$FoundModelPath = $null
foreach ($dir in $ModelSearchDirs) {
    if (-not $dir) { continue }
    foreach ($modelName in $ModelNamesToCheck) {
        $candidateModel = Join-Path $dir $modelName
        if (Test-Path -LiteralPath $candidateModel -PathType Leaf) {
            $fileInfo = Get-Item -LiteralPath $candidateModel
            if ($fileInfo.Length -gt 1048576) {
                $FoundModelPath = $candidateModel
                break
            }
        }
    }
    if ($FoundModelPath) { break }
}

if ($FoundModelPath) {
    Write-Ok "Found local Whisper model: $FoundModelPath"
} elseif ($SkipModelDownload) {
    Write-Warn "Whisper model not found, but -SkipModelDownload was specified. Skipping download."
} else {
    $AppDataModelsDir = Join-Path $env:APPDATA "app.yuko.tsukiko\models"
    $PortableModelsDir = Join-Path $ToolsDir "models"

    try {
        if (-not (Test-Path -LiteralPath $AppDataModelsDir)) {
            New-Item -ItemType Directory -Path $AppDataModelsDir -Force | Out-Null
        }
        if (-not (Test-Path -LiteralPath $PortableModelsDir)) {
            New-Item -ItemType Directory -Path $PortableModelsDir -Force | Out-Null
        }

        $ModelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/$TargetModelName"
        $DestinationModelPath = Join-Path $AppDataModelsDir $TargetModelName

        Write-Info "Whisper model not found locally. Downloading $TargetModelName from HuggingFace..."
        Write-Info "Source URL: $ModelUrl"
        Write-Info "Destination: $DestinationModelPath"

        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        # Prefer curl.exe if available on Windows 10/11 for reliable large file transfer with progress bar
        $curlCmd = Get-Command "curl.exe" -ErrorAction SilentlyContinue
        if ($curlCmd) {
            & curl.exe -L --fail --progress-bar -o "$DestinationModelPath" "$ModelUrl"
            if ($LASTEXITCODE -ne 0) {
                throw "curl.exe exited with code $LASTEXITCODE while downloading $ModelUrl"
            }
        } else {
            Invoke-WebRequest -Uri $ModelUrl -OutFile $DestinationModelPath -UseBasicParsing
        }

        $FoundModelPath = $DestinationModelPath
        Write-Ok "Downloaded Whisper model: $FoundModelPath"
    } catch {
        Write-Err "Failed to download Whisper model '$TargetModelName': $($_.Exception.Message)"
        exit 1
    }
}

# 4. Verify CLI readiness via --status
Write-Info "Verifying Tsukiko CLI status..."
try {
    $statusOutput = & $TsukikoExe --status 2>&1
    Write-Host ($statusOutput | Out-String).Trim()
    Write-Host ""
    Write-Ok "================================================================="
    Write-Ok " [OK] Tsukiko Local Speech-to-Text is READY on Windows!          "
    Write-Ok " CLI Binary : $TsukikoExe"
    if ($FoundModelPath) {
        Write-Ok " Whisper Mod: $FoundModelPath"
    }
    Write-Ok "================================================================="
} catch {
    Write-Err "Failed to run '$TsukikoExe --status': $($_.Exception.Message)"
    exit 1
}
