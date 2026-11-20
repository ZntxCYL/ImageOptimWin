param(
    [ValidateSet('win-x64')]
    [string]$Runtime = 'win-x64',
    [switch]$SkipBundling
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root 'src\ImageOptim.Win\ImageOptim.Win.csproj'
$out = Join-Path $root "dist\$Runtime"

# Regression guard: native compressor EXEs must remain external to PublishSingleFile.
[xml]$projectXml = Get-Content -LiteralPath $project -Raw
$toolsContent = @($projectXml.Project.ItemGroup.Content | Where-Object { $_.Include -eq 'tools\**\*' })
if ($toolsContent.Count -ne 1 -or $toolsContent[0].ExcludeFromSingleFile -ne 'true') {
    throw 'Invalid project: tools Content must use ExcludeFromSingleFile="true".'
}
$themePath = Join-Path $root 'src\ImageOptim.Win\Services\ThemeManager.cs'
if (-not (Get-Content -LiteralPath $themePath -Raw).Contains('using System.IO;')) {
    throw 'Invalid ThemeManager.cs: missing using System.IO;'
}

# v0.8 static release guards: avoid shipping a source regression to Debian or Windows.
$uiXaml = Get-Content -LiteralPath (Join-Path $root 'src\ImageOptim.Win\MainWindow.xaml') -Raw
$uiCode = Get-Content -LiteralPath (Join-Path $root 'src\ImageOptim.Win\MainWindow.xaml.cs') -Raw
$jobModel = Get-Content -LiteralPath (Join-Path $root 'src\ImageOptim.Win\Models\ImageJob.cs') -Raw
foreach ($requiredText in @('Window_PreviewDrag', 'Window_PreviewDrop', 'JobPrimaryText', 'JobSecondaryText', 'RenderTransformOrigin="0.5,0.5"')) {
    if (-not $uiXaml.Contains($requiredText)) { throw "v0.8 MainWindow.xaml missing: $requiredText" }
}
if (-not $uiCode.Contains('existing.ResetForRetry();')) { throw 'v0.8 re-drop handling is missing.' }
foreach ($status in @('wait','progress','ok','noopt','err')) {
    $statusFile = Join-Path $root "src\ImageOptim.Win\Assets\StatusIcons\${status}-display@2x.png"
    if (-not (Test-Path -LiteralPath $statusFile -PathType Leaf)) { throw "Missing padded status icon: $status" }
    if (-not $jobModel.Contains("${status}-display@2x.png")) { throw "Missing status icon binding: $status" }
}

# v0.8.1: verify that the C# SVG runtime resolves SVGO 3.3.2's real CLI.
$optimizer = Get-Content -LiteralPath (Join-Path $root 'src\ImageOptim.Win\Services\OptimizerService.cs') -Raw
if (-not $optimizer.Contains('"bin", "svgo"')) {
    throw 'Invalid SVG runtime: SVGO 3.3.2 uses bin/svgo (without .js).'
}

# All engines are downloaded at BUILD/PACKAGE time, not by application users.
if (-not $SkipBundling) {
    & (Join-Path $root 'scripts\bundle-engines.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Bundling failed. Published app was not created.' }
}

$required = @('oxipng.exe', 'jpegoptim.exe', 'jpegtran.exe', 'cjpeg.exe',
              'djpeg.exe', 'gifsicle.exe', 'pngquant.exe', 'node.exe',
              'svgo\node_modules\svgo\bin\svgo')
$tools = Join-Path $root 'src\ImageOptim.Win\tools'
foreach ($name in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $tools $name) -PathType Leaf)) {
        throw "Missing bundled engine: $name. Run .\scripts\bundle-engines.ps1 first."
    }
}

dotnet publish $project -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $out
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

# Separate, self-contained EXE: copied outside install directory before launching.
$updaterProject = Join-Path $root 'src\ImageOptim.Updater\ImageOptim.Updater.csproj'
$updaterOutput = Join-Path $root 'dist\updater-publish'
dotnet publish $updaterProject -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:PublishTrimmed=false `
    -p:PublishReadyToRun=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $updaterOutput
if ($LASTEXITCODE -ne 0) { throw 'Updater publish failed.' }
Copy-Item -LiteralPath (Join-Path $updaterOutput 'ImageOptim.Updater.exe') -Destination (Join-Path $out 'ImageOptim.Updater.exe') -Force
if (-not (Test-Path (Join-Path $out 'ImageOptim.Updater.exe'))) { throw 'Missing updater executable.' }
if (-not (Test-Path (Join-Path $out 'update-config.json'))) { throw 'Missing update-config.json.' }


foreach ($name in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $out "tools\$name") -PathType Leaf)) {
        throw "Publish output missing bundled engine: $name"
    }
}

Copy-Item -LiteralPath (Join-Path $root '快速使用-中文.txt') -Destination (Join-Path $out '快速使用-中文.txt') -Force
$zip = Join-Path $root "dist\ImageOptimWin-$Runtime-Bundled.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip -CompressionLevel Optimal
Write-Host "Complete offline runtime: $zip"
Write-Host 'The published ZIP contains all supported engines; no user-side downloads required.'
