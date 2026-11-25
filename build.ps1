[CmdletBinding()]
param(
    [string]$OutputDirectory = "dist"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

# Use the Windows .NET Framework compiler already present on supported Windows PCs.
$compilerCandidates = @(
    (Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"),
    (Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe")
)
$compiler = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
if (-not $compiler) {
    throw "The Windows .NET Framework C# compiler was not found. Install the .NET Framework 4.x developer tools, then run this script again."
}

$sourceDirectory = Join-Path $root "src"
$sources = @(Get-ChildItem -LiteralPath $sourceDirectory -Filter "*.cs" -File | Sort-Object Name | ForEach-Object FullName)
if ($sources.Count -eq 0) {
    throw "No C# source files were found in '$sourceDirectory'."
}

$icon = Join-Path $root "tools\bcf-manager.ico"
if (-not (Test-Path -LiteralPath $icon -PathType Leaf)) {
    throw "The application icon is missing: $icon"
}

$executable = Join-Path $root "BCF-Manager.exe"
$references = @(
    "/reference:System.Windows.Forms.dll"
    "/reference:System.Drawing.dll"
    "/reference:System.Xml.Linq.dll"
    "/reference:System.IO.Compression.dll"
    "/reference:System.IO.Compression.FileSystem.dll"
)

Write-Host "Building BCF-Manager.exe..."
& $compiler /nologo /target:winexe /optimize+ /codepage:65001 "/win32icon:$icon" "/out:$executable" $references $sources
if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE."
}

$ocrDirectory = Join-Path $root "tools\ocr"
$tessdataDirectory = Join-Path $root "tools\tessdata"
if (-not (Test-Path -LiteralPath (Join-Path $ocrDirectory "tesseract.exe") -PathType Leaf)) {
    throw "The portable OCR executable is missing from '$ocrDirectory'."
}
if (-not (Test-Path -LiteralPath (Join-Path $tessdataDirectory "eng.traineddata") -PathType Leaf)) {
    throw "The English OCR data file is missing from '$tessdataDirectory'."
}

if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $packageDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
}
else {
    $packageDirectory = [System.IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
}
[System.IO.Directory]::CreateDirectory($packageDirectory) | Out-Null
$zipPath = Join-Path $packageDirectory "BCF-Manager-Portable.zip"

# Stage only runtime files. In particular, never include the local data folder or its issues.
$stageDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("BCF-Manager-Portable-" + [Guid]::NewGuid().ToString("N"))
$stageOcr = Join-Path $stageDirectory "tools\ocr"
$stageTessdata = Join-Path $stageDirectory "tools\tessdata"
try {
    [System.IO.Directory]::CreateDirectory($stageOcr) | Out-Null
    [System.IO.Directory]::CreateDirectory($stageTessdata) | Out-Null
    Copy-Item -LiteralPath $executable -Destination (Join-Path $stageDirectory "BCF-Manager.exe")
    Get-ChildItem -LiteralPath $ocrDirectory -Force | Copy-Item -Destination $stageOcr -Recurse -Force
    Get-ChildItem -LiteralPath $tessdataDirectory -Force | Copy-Item -Destination $stageTessdata -Recurse -Force

    if (Test-Path -LiteralPath $zipPath -PathType Leaf) {
        Remove-Item -LiteralPath $zipPath -Force
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $stageDirectory,
        $zipPath,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false
    )
}
finally {
    if (Test-Path -LiteralPath $stageDirectory -PathType Container) {
        $fullStage = [System.IO.Path]::GetFullPath($stageDirectory)
        $fullTemp = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
        if (-not $fullStage.StartsWith($fullTemp, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove temporary path outside the system temp folder: $fullStage"
        }
        Remove-Item -LiteralPath $fullStage -Recurse -Force
    }
}

Write-Host ""
Write-Host "Build complete:"
Write-Host "  EXE: $executable"
Write-Host "  ZIP: $zipPath"
