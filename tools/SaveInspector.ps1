<#
.SYNOPSIS
  Cities: Skylines II save inspector: which of your enabled mods one save actually uses, and which it does not.

.DESCRIPTION
  Read-only; runs without the game. Nothing in the game folders is changed.
  * Usage: a save (.cok) is a zip whose SaveGameMetadata lists "contentPrerequisites", the mods and DLC whose
    content the city contains (the game uses it for its missing-content warning). Only that small entry is read.
  * Enabled mods: the "Enabled Mods" list the game writes to Logs\Modding.log (the active playset at the last start).
  * Asset packs: a mod whose newest folder in .cache\Mods\pdx_mods contains .cok asset files.
  Code mods (no .cok files) are listed separately and never suggested: a save does not record which code mods it needs.

  Output: a summary in the console, a CSV with every mod, and a text list of the asset packs this save does not use
  (Paradox Mods ID and name, one per line) for building a playset in Skyve. That list is also copied to the clipboard.

.PARAMETER Save
  The save to inspect (.cok). Without it, the script lists your saves and asks which one.

.EXAMPLE
  .\SaveInspector.ps1
  .\SaveInspector.ps1 -Save "$env:USERPROFILE\AppData\LocalLow\Colossal Order\Cities Skylines II\Saves\Southwell.cok"
#>
param(
    [string]$Save,
    [string]$UserData = (Join-Path $env:USERPROFILE 'AppData\LocalLow\Colossal Order\Cities Skylines II'),
    [string]$OutDir = [Environment]::GetFolderPath('Desktop'),
    [switch]$IncludeAutosaves
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Read-SaveMetadata([string]$path) {
    $zip = [IO.Compression.ZipFile]::OpenRead($path)
    try {
        $entry = $zip.Entries | Where-Object { $_.FullName -like '*.SaveGameMetadata' } | Select-Object -First 1
        if (-not $entry) { return $null }
        $r = New-Object IO.StreamReader($entry.Open())
        try { return ($r.ReadToEnd() | ConvertFrom-Json) } finally { $r.Close() }
    } finally { $zip.Dispose() }
}

function Get-ModIds($meta) {
    $ids = @()
    foreach ($p in @($meta.contentPrerequisites)) { if ("$p" -match '^Mod:(\d+)$') { $ids += $Matches[1] } }
    return $ids
}

# --- All saves (for picking, and for "also used by" on the unused list) ---
$saveRoot = Join-Path $UserData 'Saves'
$saves = New-Object System.Collections.Generic.List[object]
foreach ($f in Get-ChildItem $saveRoot -Recurse -Filter '*.cok' -File) {
    try { $meta = Read-SaveMetadata $f.FullName } catch { Write-Warning "Skipped $($f.Name): $($_.Exception.Message)"; continue }
    if (-not $meta) { continue }
    $saves.Add([pscustomobject]@{
        City = $meta.cityName; Population = $meta.population; Saved = $f.LastWriteTime
        Autosave = [bool]$meta.autoSave; File = $f.Name; Path = $f.FullName; Meta = $meta
    })
}
if ($saves.Count -eq 0) { throw "No saves found in $saveRoot" }

# --- Pick the save ---
if ($Save) {
    $full = (Resolve-Path $Save).Path
    $chosen = $saves | Where-Object { $_.Path -eq $full } | Select-Object -First 1
    if (-not $chosen) {
        $meta = Read-SaveMetadata $full
        if (-not $meta) { throw "$Save has no save metadata (is it a city save?)" }
        $chosen = [pscustomobject]@{ City = $meta.cityName; Population = $meta.population; Saved = (Get-Item $full).LastWriteTime
                                     Autosave = [bool]$meta.autoSave; File = (Split-Path $full -Leaf); Path = $full; Meta = $meta }
    }
} else {
    $list = @($saves | Where-Object { $IncludeAutosaves -or -not $_.Autosave } | Sort-Object Saved -Descending)
    if ($list.Count -eq 0) { throw 'Only autosaves found; run again with -IncludeAutosaves' }
    if (Get-Command Out-GridView -ErrorAction SilentlyContinue) {
        $chosen = $list | Select-Object City, Population, Saved, File, Path |
            Out-GridView -Title 'Pick the save to inspect, then press OK' -OutputMode Single
        if (-not $chosen) { Write-Output 'No save picked.'; return }
        $chosen = $list | Where-Object { $_.Path -eq $chosen.Path } | Select-Object -First 1
    } else {
        for ($i = 0; $i -lt $list.Count; $i++) {
            Write-Output ("{0,3}. {1} ({2:N0} people) - {3} - {4:yyyy-MM-dd HH:mm}" -f ($i + 1), $list[$i].City, $list[$i].Population, $list[$i].File, $list[$i].Saved)
        }
        $n = [int](Read-Host 'Number of the save to inspect')
        if ($n -lt 1 -or $n -gt $list.Count) { throw 'No such number' }
        $chosen = $list[$n - 1]
    }
}

# --- Enabled mods (name by id) from the last game start ---
$enabled = [ordered]@{}
$log = Join-Path $UserData 'Logs\Modding.log'
$fs = [IO.File]::Open($log, 'Open', 'Read', 'ReadWrite, Delete')
$reader = New-Object IO.StreamReader($fs)
while ($null -ne ($line = $reader.ReadLine())) {
    if ($line -match '^\s*- (.+?) \((\d+)\)\s*$') { $enabled[$Matches[2]] = $Matches[1] }
}
$reader.Close()

# --- Newest cached folder per mod id, and what it contains ---
$folders = @{}
foreach ($d in Get-ChildItem (Join-Path $UserData '.cache\Mods\pdx_mods') -Directory) {
    if ($d.Name -notmatch '^(\d+)_(\d+)$') { continue }
    $id = $Matches[1]; $ver = [int]$Matches[2]
    if (-not $folders.ContainsKey($id) -or $folders[$id].Ver -lt $ver) { $folders[$id] = @{ Ver = $ver; Path = $d.FullName } }
}
function Get-ModContent([string]$id) {
    $cok = @(); $dll = 0
    if ($folders.ContainsKey($id)) {
        $files = Get-ChildItem $folders[$id].Path -Recurse -File
        $cok = @($files | Where-Object Extension -eq '.cok')
        $dll = @($files | Where-Object Extension -eq '.dll').Count
    }
    $kind = if ($cok.Count -gt 0 -and $dll -gt 0) { 'code + assets' } elseif ($cok.Count -gt 0) { 'assets' } elseif ($dll -gt 0) { 'code' } else { 'other' }
    return @{ Kind = $kind; AssetMB = [math]::Round((($cok | Measure-Object Length -Sum).Sum) / 1MB, 1); HasAssets = $cok.Count -gt 0 }
}

# --- Which other saves use each mod ---
$otherUse = @{}
foreach ($s in $saves) {
    if ($s.Path -eq $chosen.Path -or ($s.Autosave -and -not $IncludeAutosaves)) { continue }
    foreach ($id in (Get-ModIds $s.Meta)) {
        if (-not $otherUse.ContainsKey($id)) { $otherUse[$id] = New-Object System.Collections.Generic.HashSet[string] }
        [void]$otherUse[$id].Add("$($s.City)")
    }
}

# --- Compare ---
$usedIds = @{}
foreach ($id in (Get-ModIds $chosen.Meta)) { $usedIds[$id] = $true }
$other = @(@($chosen.Meta.contentPrerequisites) | Where-Object { "$_" -notmatch '^Mod:\d+$' })

$rows = New-Object System.Collections.Generic.List[object]
foreach ($id in $enabled.Keys) {
    $c = Get-ModContent $id
    $status = if ($usedIds.ContainsKey($id)) { 'used by this save' }
              elseif (-not $c.HasAssets) { 'code mod (not assessed)' }
              else { 'NOT USED by this save' }
    $also = if ($otherUse.ContainsKey($id)) { (@($otherUse[$id]) | Sort-Object) -join '; ' } else { '' }
    $rows.Add([pscustomobject]@{ Status = $status; Id = $id; Name = $enabled[$id]; Kind = $c.Kind; AssetMB = $c.AssetMB; AlsoUsedBy = $also })
}
foreach ($id in $usedIds.Keys) {
    if ($enabled.Contains($id)) { continue }
    $rows.Add([pscustomobject]@{ Status = 'MISSING: used by this save but not enabled'; Id = $id; Name = ''; Kind = (Get-ModContent $id).Kind; AssetMB = $null; AlsoUsedBy = '' })
}

$order = @{ 'NOT USED by this save' = 0; 'MISSING: used by this save but not enabled' = 1; 'used by this save' = 2; 'code mod (not assessed)' = 3 }
$rows = @($rows | Sort-Object @{ Expression = { $order[$_.Status] } }, @{ Expression = 'AssetMB'; Descending = $true })

$unused  = @($rows | Where-Object Status -eq 'NOT USED by this save')
$used    = @($rows | Where-Object Status -eq 'used by this save')
$missing = @($rows | Where-Object Status -like 'MISSING*')
$code    = @($rows | Where-Object Status -eq 'code mod (not assessed)')
$unusedOnlyHere = @($unused | Where-Object { -not $_.AlsoUsedBy })

# --- Write results ---
$safeCity = ($chosen.City -replace '[\\/:*?"<>|]', '_')
$csv = Join-Path $OutDir "cs2_save_mods_$safeCity.csv"
$txt = Join-Path $OutDir "cs2_save_unused_$safeCity.txt"
$rows | Export-Csv -Path $csv -NoTypeInformation -Encoding UTF8
$lines = @("# Asset packs not used by $($chosen.City) ($($chosen.File)). Paradox Mods ID <tab> name <tab> MB <tab> also used by") +
         ($unused | ForEach-Object { "{0}`t{1}`t{2}`t{3}" -f $_.Id, $_.Name, $_.AssetMB, $_.AlsoUsedBy })
Set-Content -Path $txt -Value $lines -Encoding UTF8
$copied = $false
if (Get-Command Set-Clipboard -ErrorAction SilentlyContinue) { try { $lines | Set-Clipboard; $copied = $true } catch { } }

Write-Output ''
Write-Output ("Save: {0} ({1:N0} people) - {2}" -f $chosen.City, $chosen.Population, $chosen.File)
Write-Output ("Enabled mods: {0}. Used by this save: {1} ({2:N0} MB of assets). Code mods (not assessed): {3}." -f $enabled.Count, $used.Count, ($used | Measure-Object AssetMB -Sum).Sum, $code.Count)
Write-Output ("Asset packs this save does NOT use: {0} ({1:N0} MB); {2} of them ({3:N0} MB) no other save uses either." -f $unused.Count, ($unused | Measure-Object AssetMB -Sum).Sum, $unusedOnlyHere.Count, ($unusedOnlyHere | Measure-Object AssetMB -Sum).Sum)
if ($missing.Count -gt 0) { Write-Output ("Used by this save but NOT enabled: {0} mod(s) - the game will warn about missing content." -f $missing.Count) }
if ($other.Count -gt 0) { Write-Output ("Other content this save needs (DLC etc.): {0}" -f ($other -join ', ')) }
Write-Output ''
Write-Output 'Not used by this save, largest first:'
$unused | Select-Object Id, Name, AssetMB, AlsoUsedBy | Format-Table -AutoSize -Wrap | Out-String -Width 220
if ($missing.Count -gt 0) { Write-Output 'Missing:'; $missing | Select-Object Id, Kind | Format-Table -AutoSize | Out-String -Width 220 }
Write-Output "Full table: $csv"
Write-Output ("Unused list for Skyve: $txt" + $(if ($copied) { ' (also copied to the clipboard)' } else { '' }))
Write-Output 'Disable packs in a separate Skyve playset, where it is easy to undo. A pack this save does not use may still be one you want to place later.'
