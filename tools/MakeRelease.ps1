param([string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Cuphead')
# Builds the public release out\CupheadOnlineCoop-<version>.zip: one-file installer, manual-install files,
# licenses and a short readme. Uses the OFFICIAL BepInEx 5.4.22 x64 zip, unmodified (hash checked).
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
& (Join-Path $root 'build.ps1') -GamePath $GamePath
$version = ([regex]::Match((Get-Content (Join-Path $root 'src\Coop\CoopPlugin.cs') -Raw), 'Version = "([^"]+)"')).Groups[1].Value
$work = Join-Path $root "out\release\$version"
if (Test-Path $work) { Remove-Item -Recurse -Force $work }
New-Item -ItemType Directory -Force $work | Out-Null

$bep = Join-Path $root 'third_party\BepInEx_x64_5.4.22.0.zip'
if (!(Test-Path $bep)) {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest 'https://github.com/BepInEx/BepInEx/releases/download/v5.4.22/BepInEx_x64_5.4.22.0.zip' -OutFile $bep
}
if ((Get-FileHash $bep -Algorithm SHA256).Hash -ne '4C149960673F0A387BA7C016C837096AB3A41309D9140F88590BB507C59EDA3F') {
    throw 'BepInEx zip hash mismatch: delete third_party\BepInEx_x64_5.4.22.0.zip and retry.'
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$payload = Join-Path $work 'payload'
[IO.Compression.ZipFile]::ExtractToDirectory($bep, $payload)
Remove-Item (Join-Path $payload 'changelog.txt')
$plug = Join-Path $payload 'BepInEx\plugins\CupheadLanCoop'
New-Item -ItemType Directory -Force $plug | Out-Null
Copy-Item (Join-Path $root 'out\CupheadLanCoop.dll') $plug

# Installer with the payload embedded as a resource.
$zip = Join-Path $work 'payload.zip'
[IO.Compression.ZipFile]::CreateFromDirectory($payload, $zip)
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$exe = Join-Path $work 'CupheadOnlineCoop-Installer.exe'
& (Join-Path $fw 'csc.exe') /nologo /optimize+ /target:winexe "/out:$exe" "/resource:$zip,payload.zip" `
    "/reference:$fw\System.IO.Compression.dll" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll `
    (Join-Path $root 'src\Installer\Installer.cs')
if ($LASTEXITCODE) { throw 'Installer build failed.' }

$name = "CupheadOnlineCoop-$version"
$rel = Join-Path $work $name
New-Item -ItemType Directory -Force $rel | Out-Null
Copy-Item $exe $rel
Copy-Item $payload (Join-Path $rel 'manual-install') -Recurse
Copy-Item (Join-Path $root 'release\licenses') (Join-Path $rel 'licenses') -Recurse
Copy-Item (Join-Path $root 'release\README.txt') $rel
$out = Join-Path $root "out\$name.zip"
if (Test-Path $out) { Remove-Item $out }
[IO.Compression.ZipFile]::CreateFromDirectory($rel, $out)
$h = (Get-FileHash $out -Algorithm SHA256).Hash
"$h  $name.zip" | Set-Content "$out.sha256" -Encoding ASCII
"OK $out`nSHA256 $h"
