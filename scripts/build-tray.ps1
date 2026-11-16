param([string]$OutputPath)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$sourceRoot = Join-Path $projectRoot 'src\tray'
if (-not $OutputPath) { $OutputPath = Join-Path $projectRoot 'bin\SurfingTray.exe' }
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework C# compiler not found.' }

$sourceNames = @('TrayCore.cs', 'TrayApplication.cs', 'FluentControlCenter.cs', 'SurfingTray.manifest', 'SurfingTray.ico')
$wpf = Join-Path (Split-Path $compiler -Parent) 'WPF'
$hashInput = New-Object IO.MemoryStream
$utf8 = New-Object Text.UTF8Encoding($false)
foreach ($name in $sourceNames) {
    $path = Join-Path $sourceRoot $name
    $prefix = $utf8.GetBytes($name + "`n")
    $hashInput.Write($prefix, 0, $prefix.Length)
    if ($name.EndsWith('.ico')) { $bytes = [IO.File]::ReadAllBytes($path) }
    else { $bytes = $utf8.GetBytes([IO.File]::ReadAllText($path).Replace("`r`n", "`n")) }
    $hashInput.Write($bytes, 0, $bytes.Length)
}
$hasher = [Security.Cryptography.SHA256]::Create()
$sourceHash = [BitConverter]::ToString($hasher.ComputeHash($hashInput.ToArray())).Replace('-', '').ToLowerInvariant()
$hasher.Dispose()
$hashInput.Dispose()
$stamp = Join-Path ([IO.Path]::GetTempPath()) ('surfing-tray-' + [Guid]::NewGuid().ToString('N') + '.cs')
try {
    [IO.File]::WriteAllText($stamp, '[assembly: System.Reflection.AssemblyMetadata("SourceSHA256", "' + $sourceHash + '")]', $utf8)
    [IO.Directory]::CreateDirectory((Split-Path $OutputPath -Parent)) | Out-Null
    & $compiler /nologo /utf8output /codepage:65001 /target:winexe /platform:anycpu /optimize+ `
        /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Net.Http.dll `
        /reference:System.Web.Extensions.dll "/reference:$wpf\WindowsBase.dll" /reference:System.Xaml.dll `
        "/reference:$wpf\PresentationCore.dll" "/reference:$wpf\PresentationFramework.dll" `
        "/reference:$wpf\WindowsFormsIntegration.dll" "/win32manifest:$sourceRoot\SurfingTray.manifest" `
        "/win32icon:$sourceRoot\SurfingTray.ico" "/out:$OutputPath" `
        (Join-Path $sourceRoot 'TrayCore.cs') (Join-Path $sourceRoot 'TrayApplication.cs') (Join-Path $sourceRoot 'FluentControlCenter.cs') $stamp
    if ($LASTEXITCODE -ne 0) { throw 'Tray compilation failed.' }
    Write-Output "Built SurfingTray.exe (source SHA-256: $sourceHash)"
} finally {
    Remove-Item -LiteralPath $stamp -Force -ErrorAction SilentlyContinue
}
