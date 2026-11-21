function Copy-MusicPreservingExisting {
    param([string]$SourceFile, [string]$DestinationFolder)
    $name = [System.IO.Path]::GetFileName($SourceFile)
    $destination = Join-Path $DestinationFolder $name
    $sourceHash = $null
    $number = 0
    while (Test-Path -LiteralPath $destination) {
        if ($null -eq $sourceHash) { $sourceHash = (Get-FileHash -LiteralPath $SourceFile -Algorithm SHA256).Hash }
        if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -eq $sourceHash) { return }
        $number++
        $name = [System.IO.Path]::GetFileNameWithoutExtension($SourceFile) + ' (migrated ' + $number + ')' + [System.IO.Path]::GetExtension($SourceFile)
        $destination = Join-Path $DestinationFolder $name
    }
    Copy-Item -LiteralPath $SourceFile -Destination $destination -ErrorAction Stop
    Write-Host "Added music: $destination"
}
