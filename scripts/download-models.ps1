<#
.SYNOPSIS
    Downloads the two ONNX models the self-hosted face recognition provider needs.

.DESCRIPTION
    Two models are required, and they do different jobs:

      1. SCRFD  - finds faces and returns five landmarks (eyes, nose, mouth corners).
                  The landmarks matter: without them a face cannot be aligned, and an
                  unaligned crop costs far more accuracy than any threshold tuning recovers.
                  A model variant with "kps" in the name is therefore mandatory.

      2. ArcFace - turns an aligned 112x112 crop into a 512-float embedding.

    Both are published by InsightFace under the MIT licence for the code; review the model
    card for the weights before shipping commercially.

.NOTES
    These files are large (~5MB and ~250MB) and must not be committed; .gitignore already
    excludes models/*.onnx.
#>
[CmdletBinding()]
param(
    # Resolved after the param block: $PSScriptRoot is empty inside a param default on
    # Windows PowerShell 5.1.
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'

$scriptDirectory = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $scriptDirectory '..\src\Api\models' }

$models = @(
    @{
        Name = 'scrfd_500m_bnkps.onnx'
        Url  = 'https://github.com/deepinsight/insightface/releases/download/v0.7/scrfd_500m_bnkps.onnx'
        Role = 'face detection + 5 landmarks'
    },
    @{
        Name = 'arcface_r100.onnx'
        Url  = 'https://github.com/onnx/models/raw/main/validated/vision/body_analysis/arcface/model/arcfaceresnet100-11.onnx'
        Role = 'face recognition (512-d embedding)'
    }
)

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$resolved = (Resolve-Path $OutputDirectory).Path
Write-Host "Target directory: $resolved" -ForegroundColor Cyan

foreach ($model in $models) {
    $destination = Join-Path $resolved $model.Name

    if (Test-Path $destination) {
        $sizeMb = [math]::Round((Get-Item $destination).Length / 1MB, 1)
        Write-Host "  [skip] $($model.Name) already present ($sizeMb MB)" -ForegroundColor DarkGray
        continue
    }

    Write-Host "  [get ] $($model.Name) - $($model.Role)" -ForegroundColor Yellow
    Write-Host "         $($model.Url)" -ForegroundColor DarkGray

    try {
        Invoke-WebRequest -Uri $model.Url -OutFile $destination -UseBasicParsing
        $sizeMb = [math]::Round((Get-Item $destination).Length / 1MB, 1)
        Write-Host "  [ok  ] $($model.Name) ($sizeMb MB)" -ForegroundColor Green
    }
    catch {
        Write-Host "  [fail] $($model.Name): $($_.Exception.Message)" -ForegroundColor Red
        Write-Host @"

         Download it by hand and save it as:
             $destination

         If the URL has moved, any SCRFD build with landmarks and any ArcFace model
         taking 112x112 and emitting 512 floats will work. Update the paths under
         FaceRecognition:Onnx in appsettings.json to match the file names you use.
"@ -ForegroundColor DarkYellow
    }
}

Write-Host ''
Write-Host 'Next steps:' -ForegroundColor Cyan
Write-Host '  1. Set FaceRecognition:Provider to "onnx" in appsettings.Development.json'
Write-Host '  2. dotnet run --project src/Api'
Write-Host '  3. GET /api/v1/diagnostics/provider to confirm the models loaded'
Write-Host ''
Write-Host 'Re-enrol everyone after changing a model file: embeddings from different models' -ForegroundColor Yellow
Write-Host 'are not comparable, and the ProviderId change will mark existing rows Stale.' -ForegroundColor Yellow
