param(
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Cuphead',
    [switch]$SkipTests
)
# Builds the mod (out\CupheadLanCoop.dll) against YOUR local Cuphead + BepInEx install. Installs nothing.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$managed = Join-Path $GamePath 'Cuphead_Data\Managed'
$core = Join-Path $GamePath 'BepInEx\core'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (!(Test-Path $csc)) { throw '.NET Framework 4 compiler (csc.exe) not found.' }
$out = Join-Path $root 'out'
New-Item -ItemType Directory -Force $out | Out-Null
function Compile([string[]]$Arguments) {
    & $csc @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)." }
}
$src = Join-Path $root 'src\Coop'
$pure = @('InputCodec.cs', 'NetLink.cs', 'LanDiscovery.cs', 'PortOpener.cs') | ForEach-Object { Join-Path $src $_ }

if (-not $SkipTests) {
    # Pure parts (codec, framing, discovery) tested on the desktop CLR, no Unity.
    $testExe = Join-Path $out 'CoopTests.exe'
    Compile (@('/nologo', '/langversion:3', '/optimize+', '/warn:4', '/target:exe', "/out:$testExe", (Join-Path $root 'tests\CoopTests.cs')) + $pure)
    & $testExe
    if ($LASTEXITCODE -ne 0) { throw "Tests failed ($LASTEXITCODE)." }
}

# Game profile: CLR 2.0 / .NET 3.5, C# 3, local references only.
$common = @('/nologo', '/codepage:65001', '/noconfig', '/nostdlib+', '/langversion:3', '/optimize+', '/warn:4', '/platform:anycpu')
foreach ($file in @('mscorlib.dll', 'System.dll', 'System.Core.dll')) { $common += '/reference:' + (Join-Path $managed $file) }
foreach ($file in @('UnityEngine.dll', 'UnityEngine.CoreModule.dll', 'UnityEngine.UI.dll', 'UnityEngine.UIModule.dll', 'UnityEngine.IMGUIModule.dll',
        'UnityEngine.InputModule.dll', 'UnityEngine.TextRenderingModule.dll', 'UnityEngine.AudioModule.dll', 'UnityEngine.AnimationModule.dll',
        'UnityEngine.Physics2DModule.dll', 'UnityEngine.JSONSerializeModule.dll', 'Assembly-CSharp.dll', 'Assembly-CSharp-firstpass.dll', 'Rewired_Core.dll')) {
    $path = Join-Path $managed $file
    if (Test-Path $path) { $common += "/reference:$path" }
}
$common += '/reference:' + (Join-Path $core 'BepInEx.dll')
$common += '/reference:' + (Join-Path $core '0Harmony.dll')
$dll = Join-Path $out 'CupheadLanCoop.dll'
$sources = Get-ChildItem $src -Filter *.cs | Sort-Object Name | ForEach-Object { $_.FullName }
Compile ($common + @('/target:library', "/out:$dll") + $sources)
$hash = (Get-FileHash $dll -Algorithm SHA256).Hash
"OK $dll`nSHA256 $hash"
