param([Parameter(Mandatory=$true)][string]$SptRoot, [switch]$Install, [switch]$Test)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'InstallHelpers.ps1')
$SptRoot = (Resolve-Path -LiteralPath $SptRoot).Path.TrimEnd('\','/')
if (-not (Test-Path -LiteralPath (Join-Path $SptRoot 'EscapeFromTarkov.exe'))) { throw 'Choose your SPT GAME directory containing EscapeFromTarkov.exe.' }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Install a current Microsoft .NET SDK, then open PowerShell again.' }
$refs = @('BepInEx/core/BepInEx.dll','BepInEx/core/0Harmony.dll',
'EscapeFromTarkov_Data/Managed/UnityEngine.dll',
'EscapeFromTarkov_Data/Managed/UnityEngine.CoreModule.dll',
'EscapeFromTarkov_Data/Managed/UnityEngine.AudioModule.dll',
'EscapeFromTarkov_Data/Managed/UnityEngine.IMGUIModule.dll',
'EscapeFromTarkov_Data/Managed/UnityEngine.ImageConversionModule.dll',
'EscapeFromTarkov_Data/Managed/UnityEngine.TextRenderingModule.dll',
'EscapeFromTarkov_Data/Managed/UnityEngine.InputLegacyModule.dll',
'EscapeFromTarkov_Data/Managed/UnityEngine.UnityWebRequestModule.dll',
'EscapeFromTarkov_Data/Managed/UnityEngine.UnityWebRequestAudioModule.dll')
foreach ($ref in $refs) { if (-not (Test-Path -LiteralPath (Join-Path $SptRoot $ref))) { throw "Missing reference: $ref" } }
if ($Test) {
    $sdkVersion = (& dotnet --version).Trim()
    $sdkMajor = [int]($sdkVersion.Split('.')[0])
    if ($sdkMajor -lt 6) { throw 'Tests require .NET SDK 6 or newer.' }
    $testFramework = 'net' + $sdkMajor + '.0'
    $testProject = Join-Path $PSScriptRoot 'Tests/StateTests.csproj'
    $frameworkProperty = "-p:SoundtrackTestFramework=$testFramework"
    Write-Host "Testing with SDK $sdkVersion targeting $testFramework"
    & dotnet restore $testProject $frameworkProperty
    if ($LASTEXITCODE -ne 0) { throw 'Test dependency restore failed; nothing has been installed. Send the output.' }
    & dotnet build $testProject -c Release --no-restore $frameworkProperty
    if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed; nothing has been installed. Send the output.' }
    $testAssembly = Join-Path $PSScriptRoot "Tests/bin/Release/$testFramework/StateTests.dll"
    & dotnet $testAssembly
    if ($LASTEXITCODE -ne 0) { throw 'Automated tests failed; nothing has been installed. Send the output.' }
    & (Join-Path $PSScriptRoot 'Tests/MigrationTests.ps1')
}
& dotnet build (Join-Path $PSScriptRoot 'CustomSoundtrack.csproj') -c Release "-p:SptRoot=$SptRoot"
if ($LASTEXITCODE -ne 0) { throw 'Build failed. Send the error output for troubleshooting.' }
$buildLabel = 'dist-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff')
$package = Join-Path (Join-Path $PSScriptRoot $buildLabel) 'BepInEx/plugins/CustomSoundtrack'
$folders = @('MainMenu','Extraction','Death','RaidAmbient','Personal','Combat','Hideout')
$folders += @('Shared','Killa','Tagilla','Reshala','Shturman','Glukhar','Sanitar','Kaban','Kollontay','Knight','BigPipe','Birdeye','Zryachiy','Partisan') | ForEach-Object { "Bosses/$_" }
foreach ($folder in $folders) {
    $outFolder = Join-Path (Join-Path $package 'Music') $folder
    New-Item -ItemType Directory -Force -Path $outFolder | Out-Null
    $inFolder = Join-Path (Join-Path $PSScriptRoot 'Music') $folder
    if (Test-Path -LiteralPath $inFolder) {
        foreach ($song in (Get-ChildItem -LiteralPath $inFolder -File | Where-Object { $_.Extension.ToLowerInvariant() -in @('.wav','.ogg','.mp3') })) {
            try { Copy-Item -LiteralPath $song.FullName -Destination $outFolder -ErrorAction Stop }
            catch { Write-Warning "Could not package optional song '$($song.Name)': $($_.Exception.Message)" }
        }
    }
}
# Package custom notification sounds and instructions using relative folders.
$soundsRoot = Join-Path $PSScriptRoot 'Sounds'
if (Test-Path -LiteralPath $soundsRoot) {
    Copy-Item -LiteralPath $soundsRoot -Destination (Join-Path $package 'Sounds') -Recurse -Force
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'bin/Release/netstandard2.1/CustomSoundtrack.dll') -Destination $package -Force
Write-Host "Built package: $package"
if ($Install) {
    if (Get-Process -Name EscapeFromTarkov -ErrorAction SilentlyContinue) { throw 'Close Tarkov before installing; built package remains in dist.' }
    $pluginsRoot = Join-Path $SptRoot 'BepInEx/plugins'
    $target = Join-Path $pluginsRoot 'CustomSoundtrack'
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    $legacyDlls = @(Get-ChildItem -LiteralPath $pluginsRoot -Recurse -File -Filter 'HideoutRadio.dll')
    # Copy user music before changing any active DLL. A failed copy aborts installation.
    foreach ($folder in $folders) {
        $dest = Join-Path (Join-Path $target 'Music') $folder
        New-Item -ItemType Directory -Force -Path $dest | Out-Null
        foreach ($song in (Get-ChildItem -LiteralPath (Join-Path (Join-Path $package 'Music') $folder) -File)) {
            Copy-MusicPreservingExisting -SourceFile $song.FullName -DestinationFolder $dest
        }
    }
    $oldFolders = @((Join-Path $pluginsRoot 'CustomHideoutRadio/Music')) + @($legacyDlls | ForEach-Object { Join-Path $_.DirectoryName 'Music' })
    $hideoutFolder = Join-Path $target 'Music/Hideout'
    foreach ($oldMusic in ($oldFolders | Sort-Object -Unique)) {
        if (Test-Path -LiteralPath $oldMusic) {
            foreach ($song in (Get-ChildItem -LiteralPath $oldMusic -File | Where-Object { $_.Extension.ToLowerInvariant() -in @('.wav','.ogg','.mp3') })) {
                Copy-MusicPreservingExisting -SourceFile $song.FullName -DestinationFolder $hideoutFolder
            }
        }
    }
    $dll = Join-Path $target 'CustomSoundtrack.dll'
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
    $configFile = Join-Path $SptRoot 'BepInEx/config/com.wrussell.customsoundtrack.cfg'
    if (Test-Path -LiteralPath $configFile) { Copy-Item -LiteralPath $configFile -Destination ($configFile + '.' + $stamp + '.previous') -ErrorAction Stop }
    $hadPrevious = Test-Path -LiteralPath $dll
    $backup = $dll + '.' + $stamp + '.previous'
    if ($hadPrevious) { Copy-Item -LiteralPath $dll -Destination $backup -ErrorAction Stop }
    $disabledRadios = @()
    try {
        foreach ($legacy in $legacyDlls) {
            $disabled = $legacy.FullName + '.' + $stamp + '.disabled'
            Move-Item -LiteralPath $legacy.FullName -Destination $disabled -ErrorAction Stop
            $disabledRadios += [pscustomobject]@{ Original = $legacy.FullName; Disabled = $disabled }
            Write-Host "Backed up and disabled standalone radio: $disabled"
        }
        Copy-Item -LiteralPath (Join-Path $package 'CustomSoundtrack.dll') -Destination $dll -Force -ErrorAction Stop
    }
    catch {
        $installError = $_
        # Restore the previous active binaries if the replacement fails.
        $rollbackErrors = @()
        try {
            if ($hadPrevious) { Copy-Item -LiteralPath $backup -Destination $dll -Force }
            elseif (Test-Path -LiteralPath $dll) { Remove-Item -LiteralPath $dll }
        } catch { $rollbackErrors += $_.Exception.Message }
        foreach ($radio in $disabledRadios) {
            try { Move-Item -LiteralPath $radio.Disabled -Destination $radio.Original -Force }
            catch { $rollbackErrors += $_.Exception.Message }
        }
        if ($rollbackErrors.Count -gt 0) { throw "Installation failed; rollback needs attention. Backups remain. Original error: $installError. Restore errors: $($rollbackErrors -join '; ')" }
        throw "Installation failed; previous DLLs restored. $installError"
    }
    $packSounds = Join-Path $package 'Sounds'
    if (Test-Path -LiteralPath $packSounds) {
        foreach ($soundFile in (Get-ChildItem -LiteralPath $packSounds -Recurse -File)) {
            $relativeSound = $soundFile.FullName.Substring($packSounds.Length).TrimStart([char[]]'\/')
            $soundDestination = Join-Path (Join-Path $target 'Sounds') $relativeSound
            $soundDirectory = Split-Path -Parent $soundDestination
            New-Item -ItemType Directory -Force -Path $soundDirectory | Out-Null
            if ($soundFile.Extension.ToLowerInvariant() -in @('.wav','.ogg','.mp3')) {
                # Exact filenames are event IDs. Do not create ineffective '(migrated)' copies.
                if (-not (Test-Path -LiteralPath $soundDestination)) {
                    Copy-Item -LiteralPath $soundFile.FullName -Destination $soundDestination
                } elseif ((Get-FileHash -LiteralPath $soundFile.FullName).Hash -ne (Get-FileHash -LiteralPath $soundDestination).Hash) {
                    Write-Warning "Existing UI sound kept: $soundDestination. Replace it manually to use the new file."
                }
            } elseif (-not (Test-Path -LiteralPath $soundDestination)) {
                Copy-Item -LiteralPath $soundFile.FullName -Destination $soundDestination
            }
        }
    }
    Write-Host "Installed unified Custom Soundtrack 0.5.0: $target"
    Write-Host 'One plugin now handles menus, hideout and raids. Old music/config files are retained.'
    Write-Host 'Radio settings migrate into the Custom Soundtrack configuration on first launch.'
    Write-Host 'Confirm BepInEx loads Custom Soundtrack 0.5.0 and no separate Custom Hideout Radio entry.'
}
