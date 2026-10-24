# Builds the mod and makes the release zip in out\.
# Usage: .\build.ps1 [-GameDir "D:\SteamLibrary\steamapps\common\Real VR Fishing"]
# The game folder needs BepInEx 5 installed once, because the build references its DLLs and the game's.
param([string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Real VR Fishing")
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$bepVersion = "5.4.23.5"
$bepZip = Join-Path $root "out\cache\BepInEx_win_x64_$bepVersion.zip"

foreach ($p in "RealVRFishingPCMode", "RealVRFishingPCMode.Patcher") {
    dotnet build "$root\src\$p\$p.csproj" -c Release -p:GameDir="$GameDir" -nologo -v q
    if ($LASTEXITCODE) { throw "build of $p failed" }
}
$version = (Select-String -Path "$root\src\RealVRFishingPCMode\Plugin.cs" -Pattern 'Version = "([^"]+)"').Matches[0].Groups[1].Value

if (-not (Test-Path $bepZip)) {
    New-Item -ItemType Directory -Force (Split-Path $bepZip) | Out-Null
    Invoke-WebRequest "https://github.com/BepInEx/BepInEx/releases/download/v$bepVersion/BepInEx_win_x64_$bepVersion.zip" -OutFile $bepZip
}

$stage = Join-Path $root "out\stage"
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
Expand-Archive $bepZip $stage
New-Item -ItemType Directory -Force "$stage\BepInEx\plugins", "$stage\BepInEx\patchers" | Out-Null
Copy-Item "$root\src\RealVRFishingPCMode\bin\Release\net472\RealVRFishingPCMode.dll" "$stage\BepInEx\plugins\"
Copy-Item "$root\src\RealVRFishingPCMode.Patcher\bin\Release\net472\RealVRFishingPCMode.Patcher.dll" "$stage\BepInEx\patchers\"
# Windows text files: CRLF line endings, so cmd and Notepad read them right
foreach ($f in Get-ChildItem "$root\package" -File) {
    $text = [IO.File]::ReadAllText($f.FullName) -replace "`r?`n", "`r`n"
    [IO.File]::WriteAllText((Join-Path $stage $f.Name), $text)
}
Copy-Item "$root\LICENSE" "$stage\BepInEx\plugins\RealVRFishingPCMode-LICENSE.txt"
Copy-Item "$root\THIRD-PARTY-NOTICES.txt" "$stage\BepInEx\plugins\RealVRFishingPCMode-THIRD-PARTY-NOTICES.txt"

$zip = Join-Path $root "out\RealVRFishingPCMode-v$version.zip"
Remove-Item $zip -ErrorAction SilentlyContinue
# zip by hand: Compress-Archive skips dot files and writes backslash paths some unzippers misread
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$z = [IO.Compression.ZipFile]::Open($zip, 'Create')
foreach ($f in Get-ChildItem $stage -Recurse -File -Force) {
    $name = $f.FullName.Substring($stage.Length + 1).Replace('\', '/')
    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($z, $f.FullName, $name, 'Optimal') | Out-Null
}
$z.Dispose()
"built $zip"
