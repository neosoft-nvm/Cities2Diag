<#
.SYNOPSIS
  Game-wide asset audit for Cities: Skylines II: which enabled Paradox Mods asset packs are not used by any of your saves.

.DESCRIPTION
  Read-only. Nothing in the game folders is changed.
  * Enabled mods: the "Enabled Mods" list the game writes to Logs\Modding.log (the active playset at the last start).
  * Asset packs: the mod's newest folder in .cache\Mods\pdx_mods contains .cok asset files (size = their total size).
  * Usage: every save (Saves\**\*.cok) is a zip whose SaveGameMetadata lists "contentPrerequisites" — the mods whose
    content the city contains (the game uses it for its missing-content warning). Only that small entry is read.
  A pack that no save lists is a candidate to disable. Code mods (no .cok) are never suggested.

.PARAMETER OutCsv
  Where to write the full table (default: Desktop\cs2_asset_audit.csv).
#>
param(
    [string]$UserData = (Join-Path $env:USERPROFILE 'AppData\LocalLow\Colossal Order\Cities Skylines II'),
    [string]$OutCsv = (Join-Path ([Environment]::GetFolderPath('Desktop')) 'cs2_asset_audit.csv'),
    [switch]$IncludeAutosaves
)

Add-Type -AssemblyName System.IO.Compression.FileSystem

# Enabled mods (name, id) from the last game start.
$enabled = [ordered]@{}
$log = Join-Path $UserData 'Logs\Modding.log'
$fs = [IO.File]::Open($log, 'Open', 'Read', 'ReadWrite, Delete')
$reader = New-Object IO.StreamReader($fs)
while ($null -ne ($line = $reader.ReadLine())) {
    if ($line -match '^\s*- (.+?) \((\d+)\)\s*$') { $enabled[$Matches[2]] = $Matches[1] }
}
$reader.Close()

# Newest cached folder per mod id.
$folders = @{}
foreach ($d in Get-ChildItem (Join-Path $UserData '.cache\Mods\pdx_mods') -Directory) {
    if ($d.Name -notmatch '^(\d+)_(\d+)$') { continue }
    $id = $Matches[1]; $ver = [int]$Matches[2]
    if (-not $folders.ContainsKey($id) -or $folders[$id].Ver -lt $ver) { $folders[$id] = @{ Ver = $ver; Path = $d.FullName } }
}

# Which saves use which mods.
$usedBy = @{}
$saves = @()
foreach ($f in Get-ChildItem (Join-Path $UserData 'Saves') -Recurse -Filter '*.cok' -File) {
    try {
        $zip = [IO.Compression.ZipFile]::OpenRead($f.FullName)
        $entry = $zip.Entries | Where-Object { $_.FullName -like '*.SaveGameMetadata' } | Select-Object -First 1
        if (-not $entry) { $zip.Dispose(); continue }
        $r = New-Object IO.StreamReader($entry.Open()); $meta = $r.ReadToEnd() | ConvertFrom-Json; $r.Close(); $zip.Dispose()
    } catch { Write-Warning "Skipped $($f.Name): $($_.Exception.Message)"; continue }
    if ($meta.autoSave -and -not $IncludeAutosaves) { continue }
    $label = "$($meta.cityName) ($($f.BaseName))"
    $saves += [pscustomobject]@{ Save = $f.Name; City = $meta.cityName; Population = $meta.population; Date = $f.LastWriteTime; Mods = @($meta.contentPrerequisites).Count }
    foreach ($p in $meta.contentPrerequisites) {
        if ($p -notmatch '^Mod:(\d+)$') { continue }
        if (-not $usedBy.ContainsKey($Matches[1])) { $usedBy[$Matches[1]] = New-Object System.Collections.Generic.List[string] }
        $usedBy[$Matches[1]].Add($label)
    }
}

$rows = foreach ($id in $enabled.Keys) {
    $folder = $folders[$id]
    $cok = @(); $dll = 0
    if ($folder) {
        $files = Get-ChildItem $folder.Path -Recurse -File
        $cok = @($files | Where-Object Extension -eq '.cok')
        $dll = @($files | Where-Object Extension -eq '.dll').Count
    }
    $mb = [math]::Round((($cok | Measure-Object Length -Sum).Sum) / 1MB, 1)
    $cities = if ($usedBy.ContainsKey($id)) { ($usedBy[$id] | Sort-Object -Unique) -join '; ' } else { '' }
    $kind = if ($cok.Count -gt 0 -and $dll -gt 0) { 'code + assets' } elseif ($cok.Count -gt 0) { 'assets' } elseif ($dll -gt 0) { 'code' } else { 'other' }
    [pscustomobject]@{
        Id = $id; Name = $enabled[$id]; Kind = $kind; AssetFiles = $cok.Count; AssetMB = $mb
        UsedInSaves = if ($usedBy.ContainsKey($id)) { ($usedBy[$id] | Sort-Object -Unique).Count } else { 0 }
        Status = if ($cok.Count -eq 0) { 'code mod (not assessed)' } elseif ($cities) { 'used' } else { 'NOT USED in any save' }
        Cities = $cities
    }
}
$rows = $rows | Sort-Object @{ Expression = { $_.Status -ne 'NOT USED in any save' } }, @{ Expression = 'AssetMB'; Descending = $true }
$rows | Export-Csv -Path $OutCsv -NoTypeInformation -Encoding UTF8

$unused = @($rows | Where-Object Status -eq 'NOT USED in any save')
Write-Output ("Saves checked: {0} ({1})" -f $saves.Count, (($saves | ForEach-Object { "$($_.City) $($_.Save)" }) -join ', '))
Write-Output ("Enabled mods: {0}; asset packs: {1}; not used in any save: {2} ({3:N0} MB)" -f $enabled.Count,
    @($rows | Where-Object AssetFiles -gt 0).Count, $unused.Count, ($unused | Measure-Object AssetMB -Sum).Sum)
Write-Output "Full table: $OutCsv"
$unused | Select-Object Id, Name, Kind, AssetMB | Format-Table -AutoSize | Out-String -Width 200
