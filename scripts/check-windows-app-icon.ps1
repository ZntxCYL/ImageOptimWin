param(
    [string]$AppDirectory = "dist/win-x64"
)
$ErrorActionPreference = 'Stop'
$exe = Join-Path (Resolve-Path -LiteralPath $AppDirectory).Path 'ImageOptim.Win.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw "ImageOptim.Win.exe missing: $exe"
}
Add-Type -AssemblyName System.Drawing
$icon = [System.Drawing.Icon]::ExtractAssociatedIcon($exe)
if ($null -eq $icon) { throw 'Windows EXE has no embedded application icon.' }
try {
    $image = $icon.ToBitmap()
    try {
        if ($image.Width -lt 16 -or $image.Height -lt 16) {
            throw "Embedded app icon dimensions invalid: $($image.Width)x$($image.Height)"
        }
        foreach ($corner in @(
            @('top-left',0,0),
            @('top-right',($image.Width - 1),0),
            @('bottom-left',0,($image.Height - 1)),
            @('bottom-right',($image.Width - 1),($image.Height - 1))
        )) {
            $pixel = $image.GetPixel([int]$corner[1], [int]$corner[2])
            Write-Host "Embedded EXE icon $($corner[0]): A=$($pixel.A) R=$($pixel.R) G=$($pixel.G) B=$($pixel.B)"
            if ($pixel.A -ne 0) {
                throw "Embedded EXE icon corner $($corner[0]) is opaque. Transparent icon/AND mask was not embedded."
            }
        }
        Write-Host "[PASS] Windows EXE icon was extracted with transparent corners."
    }
    finally { $image.Dispose() }
}
finally { $icon.Dispose() }
