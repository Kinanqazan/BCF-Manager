$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw 'The Windows .NET Framework C# compiler was not found.'
}

$sources = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' -File |
    ForEach-Object FullName
$icon = Join-Path $PSScriptRoot 'tools\bcf-manager.ico'
$output = Join-Path $PSScriptRoot 'BCF-Manager.exe'
$references = @(
    '/reference:System.Windows.Forms.dll'
    '/reference:System.Drawing.dll'
    '/reference:System.Xml.Linq.dll'
    '/reference:System.IO.Compression.dll'
    '/reference:System.IO.Compression.FileSystem.dll'
)

& $compiler /nologo /target:winexe /optimize+ /codepage:65001 "/win32icon:$icon" "/out:$output" $references $sources
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }
