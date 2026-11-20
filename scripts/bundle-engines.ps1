# Bundles every optimizer used by ImageOptimWin into the publish output.
# Build-time network access is required ONCE. The resulting ZIP works offline.
# Only Windows x64 is supported by this pinned engine manifest.
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$target = Join-Path $root 'src\ImageOptim.Win\tools'
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("ImageOptimWin-Bundle-" + [Guid]::NewGuid().ToString('N'))
$staging = Join-Path $tmp 'engines'
New-Item -ItemType Directory -Force -Path $staging | Out-Null

function Get-VerifiedArchive {
    param([string]$Name, [string]$Url, [string]$Sha256)
    $file = Join-Path $tmp $Name
    Write-Host "Fetching $Name ..."
    Invoke-WebRequest -Uri $Url -OutFile $file -MaximumRedirection 8
    $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $Sha256.ToLowerInvariant()) {
        Remove-Item -LiteralPath $file -Force
        throw "SHA256 mismatch for $Name (got $hash). Aborting."
    }
    return $file
}
function Expand-Asset {
    param([string]$Name, [string]$Url, [string]$Sha256)
    $file = Get-VerifiedArchive $Name $Url $Sha256
    $dir = Join-Path $tmp ([IO.Path]::GetFileNameWithoutExtension($Name))
    Expand-Archive -LiteralPath $file -DestinationPath $dir -Force
    return $dir
}
function Copy-Binary {
    param([string]$ExtractedPath, [string]$OriginalName, [string]$NameInApp)
    $file = Get-ChildItem -LiteralPath $ExtractedPath -Recurse -File -Filter $OriginalName |
        Select-Object -First 1
    if (-not $file) { throw "$OriginalName not found in $ExtractedPath" }
    $out = Join-Path $staging $NameInApp
    Copy-Item -LiteralPath $file.FullName -Destination $out -Force
    # Reject empty, incorrectly named, or non-PE downloads.
    if ((Get-Item -LiteralPath $out).Length -lt 1024) { throw "Suspicious executable: $out" }
    $stream = [IO.File]::OpenRead($out)
    try {
        if ($stream.ReadByte() -ne 77 -or $stream.ReadByte() -ne 90) {
            throw "Expected a native Windows EXE: $out"
        }
    } finally { $stream.Dispose() }
    # Preserve adjacent DLL dependencies if the upstream distribution needs them.
    Get-ChildItem -LiteralPath $file.DirectoryName -File -Filter '*.dll' |
        ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $staging -Force }
    Write-Host ("Bundled {0} ({1} bytes)" -f $NameInApp, (Get-Item $out).Length)
}
function Get-SourceArchive {
    param([string]$Name, [string]$Url, [string]$Sha256 = '')
    $dir = Join-Path $staging 'licenses\corresponding-source'
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $file = Join-Path $dir $Name
    Invoke-WebRequest -Uri $Url -OutFile $file -MaximumRedirection 8
    if (-not [string]::IsNullOrWhiteSpace($Sha256)) {
        $actual = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $Sha256.ToLowerInvariant()) { throw "Source archive SHA256 mismatch: $Name" }
    }
}

try {
    # 1. PNG lossless, official Rust release (MIT)
    $ox = Expand-Asset 'oxipng-10.2.1-win64.zip' `
        'https://github.com/oxipng/oxipng/releases/download/v10.2.1/oxipng-10.2.1-x86_64-pc-windows-msvc.zip' `
        '7e940f83ee46874b73f53031f96a15834cb70b220af27391fb06fe7b4dd798e1'
    Copy-Binary $ox 'oxipng.exe' 'oxipng.exe'

    # 2. JPEG optimizations, official jpegoptim release (GPL-3.0-only)
    $jo = Expand-Asset 'jpegoptim-1.5.6-win64.zip' `
        'https://github.com/tjko/jpegoptim/releases/download/v1.5.6/jpegoptim-1.5.6-x64-windows.zip' `
        'db2d8caee88f2665b772c4591cba223b8b729500b88266dc06d4bf79d28fe11a'
    Copy-Binary $jo 'jpegoptim.exe' 'jpegoptim.exe'

    # 3. MozJPEG: prebuilt Windows binary, with cjpeg/djpeg and lossless jpegtran.
    $moz = Expand-Asset 'mozjpeg-4.1.5-win64.zip' `
        'https://github.com/garyzyg/mozjpeg-windows/releases/download/4.1.5/mozjpeg-x64.zip' `
        '176ef4f632b09dca449e27bc11df090c1222528dff9f583a14bbd026a0c8eb2f'
    Copy-Binary $moz 'jpegtran-static.exe' 'jpegtran.exe'
    Copy-Binary $moz 'cjpeg-static.exe' 'cjpeg.exe'
    Copy-Binary $moz 'djpeg-static.exe' 'djpeg.exe'

    # 4. GIF lossless, maintained Windows port
    $gif = Expand-Asset 'gifsicle-1.97-win64.zip' `
        'https://eternallybored.org/misc/gifsicle/releases/gifsicle-1.97-win64.zip' `
        'bb3ad4e1521a3953152f406eaafabda153fb5986938405860a7e7f12a3d13227'
    Copy-Binary $gif 'gifsicle.exe' 'gifsicle.exe'

    # 5. PNG lossy; bundled but off by default (GPL/commercial dual-license)
    $pq = Expand-Asset 'pngquant-2.18.0-win.zip' `
        'https://pngquant.org/pngquant-windows.zip' `
        'bd0257aeeccfe446a4cd764927e26f8af6051796f28abed104307284107b120d'
    Copy-Binary $pq 'pngquant.exe' 'pngquant.exe'

    # 6. SVG: ship both the SVGO CLI and its portable Node.js runtime.
    # No Node installation and no npm install is required on end-user machines.
    $nodeDist = Expand-Asset 'node-v22.16.0-win-x64.zip' `
        'https://nodejs.org/dist/v22.16.0/node-v22.16.0-win-x64.zip' `
        '21c2d9735c80b8f86dab19305aa6a9f6f59bbc808f68de3eef09d5832e3bfbbd'
    Copy-Binary $nodeDist 'node.exe' 'node.exe'
    $npm = Get-ChildItem -LiteralPath $nodeDist -Recurse -File -Filter 'npm.cmd' | Select-Object -First 1
    if (-not $npm) { throw 'Portable Node archive did not include npm.cmd' }
    $svgoDir = Join-Path $staging 'svgo'
    New-Item -ItemType Directory -Force -Path $svgoDir | Out-Null
    & $npm.FullName install --prefix $svgoDir 'svgo@3.3.2' --omit=dev --ignore-scripts --no-audit --no-fund --save-exact
    if ($LASTEXITCODE -ne 0) { throw "npm could not install the pinned SVGO version ($LASTEXITCODE)" }
    # SVGO 3.3.2 declares "bin": "./bin/svgo" in its package.json (no .js suffix).
    $svgoPackage = Join-Path $svgoDir 'node_modules\svgo\package.json'
    if (-not (Test-Path -LiteralPath $svgoPackage -PathType Leaf)) { throw 'SVGO package.json missing after npm install' }
    $svgoManifest = Get-Content -LiteralPath $svgoPackage -Raw | ConvertFrom-Json
    if ($svgoManifest.version -ne '3.3.2' -or $svgoManifest.bin -ne './bin/svgo') {
        throw "Unexpected SVGO package version or CLI entrypoint: $($svgoManifest.version) / $($svgoManifest.bin)"
    }
    $svgoCli = Join-Path $svgoDir 'node_modules\svgo\bin\svgo'
    if (-not (Test-Path -LiteralPath $svgoCli -PathType Leaf)) { throw 'SVGO CLI missing after npm install' }

    # Include source archives for bundled GPL engines, not just links to them.
    Get-SourceArchive 'jpegoptim-1.5.6.tar.gz' `
        'https://github.com/tjko/jpegoptim/releases/download/v1.5.6/jpegoptim-1.5.6.tar.gz' `
        '661a808dfffa933d78c6beb47a2937d572b9f03e94cbaaab3d4c0d72f410e9be'
    Get-SourceArchive 'pngquant-2.18.0-src.tar.gz' `
        'https://pngquant.org/pngquant-2.18.0-src.tar.gz' `
        'e72194b52b36f040deaec49a1ddd5dcd8d4feecc3a5fe6c5e9589a9707b233d4'
    # Source from upstream, version matched to the bundled Windows port.
    Get-SourceArchive 'gifsicle-1.97.tar.gz' `
        'https://www.lcdf.org/gifsicle/gifsicle-1.97.tar.gz'

    $notice = Join-Path $root 'THIRD_PARTY.md'
    Copy-Item -LiteralPath $notice -Destination (Join-Path $staging 'THIRD_PARTY.md') -Force
    # Preserve original LICENSE files included in the upstream archives where available.
    $licenseRoot = Join-Path $staging 'licenses\from-vendor-archives'
    New-Item -ItemType Directory -Force -Path $licenseRoot | Out-Null
    foreach ($entry in @(@('oxipng',$ox), @('jpegoptim',$jo), @('mozjpeg',$moz), @('gifsicle',$gif), @('pngquant',$pq))) {
        $license = Get-ChildItem -LiteralPath $entry[1] -Recurse -File |
            Where-Object { $_.Name -match '^(LICENSE|COPYING|COPYRIGHT)(\.|$)' } | Select-Object -First 1
        if ($license) {
            Copy-Item -LiteralPath $license.FullName -Destination (Join-Path $licenseRoot ($entry[0] + '-' + $license.Name))
        }
    }
    # Ensure the application doesn't accidentally run with a partially populated tool directory.
    $expected = @('oxipng.exe','jpegoptim.exe','jpegtran.exe','cjpeg.exe',
        'djpeg.exe','gifsicle.exe','pngquant.exe','node.exe','svgo\node_modules\svgo\bin\svgo')
    foreach ($name in $expected) {
        if (-not (Test-Path -LiteralPath (Join-Path $staging $name) -PathType Leaf)) {
            throw "Bundled app is incomplete: $name"
        }
    }
    # Save existing tracked README but swap generated binaries as a set.
    $readmeFile = Join-Path $target 'README.txt'
    $readmeText = if (Test-Path $readmeFile) { Get-Content -LiteralPath $readmeFile -Raw } else { '' }
    if (Test-Path $target) { Remove-Item -LiteralPath $target -Recurse -Force }
    Copy-Item -LiteralPath $staging -Destination $target -Recurse -Force
    Set-Content -LiteralPath (Join-Path $target 'README.txt') -Value $readmeText -Encoding UTF8
    Write-Host "All required engines prepared under $target"
} finally {
    if (Test-Path $tmp) { Remove-Item -LiteralPath $tmp -Recurse -Force }
}
