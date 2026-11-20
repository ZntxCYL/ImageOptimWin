# Intended to execute in GitHub Actions on a Windows runner after publish.
[CmdletBinding()]
param([string]$AppDirectory = 'dist\win-x64')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$tools = Join-Path (Join-Path $root $AppDirectory) 'tools'
$required = @('oxipng.exe','jpegoptim.exe','jpegtran.exe','cjpeg.exe',
    'djpeg.exe','gifsicle.exe','pngquant.exe','node.exe','svgo\node_modules\svgo\bin\svgo')
foreach ($name in $required) {
    if (-not (Test-Path (Join-Path $tools $name) -PathType Leaf)) {
        throw "Missing: $name"
    }
}
function Invoke-Tool {
    param([string]$Exe, [string[]]$CliArgs)
    Write-Host "Checking $Exe $($CliArgs -join ' ')"
    & (Join-Path $tools $Exe) @CliArgs
    if ($LASTEXITCODE -ne 0) { throw "$Exe returned exit code $LASTEXITCODE" }
}
Invoke-Tool 'oxipng.exe' @('--version')
Invoke-Tool 'jpegoptim.exe' @('--version')
Invoke-Tool 'jpegtran.exe' @('-version')
Invoke-Tool 'cjpeg.exe' @('-version')
Invoke-Tool 'djpeg.exe' @('-version')
Invoke-Tool 'gifsicle.exe' @('--version')
Invoke-Tool 'pngquant.exe' @('--version')
Invoke-Tool 'node.exe' @('--version')

$temp = Join-Path ([IO.Path]::GetTempPath()) ('ImageOptimWin-smoke-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp -Force | Out-Null
try {
    $svgInput = Join-Path $temp 'input.svg'
    $svgOutput = Join-Path $temp 'output.svg'
    [IO.File]::WriteAllText($svgInput,
        '<svg xmlns="http://www.w3.org/2000/svg" width="100" height="100"><rect width="100" height="100" fill="#ff0000"/></svg>')
    $script = Join-Path $tools 'svgo\node_modules\svgo\bin\svgo'
    & (Join-Path $tools 'node.exe') $script --input $svgInput --output $svgOutput --multipass
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $svgOutput -PathType Leaf)) {
        throw 'SVGO compression smoke test failed'
    }
    [xml]$result = Get-Content $svgOutput -Raw
    if ($result.DocumentElement.LocalName -ne 'svg') { throw 'SVGO produced non-SVG output' }
    Write-Host 'Engine/runtime smoke test succeeded.'
} finally {
    if (Test-Path $temp) { Remove-Item -LiteralPath $temp -Recurse -Force }
}
