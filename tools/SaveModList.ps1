<#
.SYNOPSIS
  Exports just the mods one save uses (its "contentPrerequisites"), as .txt, .csv and a Skyve playset .json.

.DESCRIPTION
  Read-only; runs without the game. Writes "<save name> - mods used by save" .txt (ID <tab> name), .csv and .json
  (Skyve playset export format) to the Desktop. A save records the mods whose content it contains (asset packs and mods
  that add assets); it does not record code-only mods, so those are not in the list.

.EXAMPLE
  .\SaveModList.ps1 -Save "$env:USERPROFILE\AppData\LocalLow\Colossal Order\Cities Skylines II\Saves\76561197960271872\Starford 2.cok"
#>
param(
    [Parameter(Mandatory)] [string]$Save,
    [string]$UserData = (Join-Path $env:USERPROFILE 'AppData\LocalLow\Colossal Order\Cities Skylines II'),
    [string]$OutDir = [Environment]::GetFolderPath('Desktop')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path $Save).Path)
try {
    $entry = $zip.Entries | Where-Object { $_.FullName -like '*.SaveGameMetadata' } | Select-Object -First 1
    if (-not $entry) { throw "$Save has no save metadata" }
    $reader = New-Object IO.StreamReader($entry.Open())
    try { $meta = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Close() }
} finally { $zip.Dispose() }

$ids = @(); $other = @()
foreach ($p in @($meta.contentPrerequisites)) {
    if ("$p" -match '^Mod:(\d+)$') { $ids += $Matches[1] } else { $other += "$p" }
}

# Names from the enabled-mods list of the last game start; versions from the mod cache.
$names = @{}
$fs = [IO.File]::Open((Join-Path $UserData 'Logs\Modding.log'), 'Open', 'Read', 'ReadWrite, Delete')
$logReader = New-Object IO.StreamReader($fs)
while ($null -ne ($line = $logReader.ReadLine())) {
    if ($line -match '^\s*- (.+?) \((\d+)\)\s*$') { $names[$Matches[2]] = ($Matches[1] -replace ' v[\w\.\-]*$', '') }
}
$logReader.Close()
$ver = @{}
foreach ($d in Get-ChildItem (Join-Path $UserData '.cache\Mods\pdx_mods') -Directory) {
    if ($d.Name -match '^(\d+)_(\d+)$' -and (-not $ver.ContainsKey($Matches[1]) -or $ver[$Matches[1]] -lt [int]$Matches[2])) { $ver[$Matches[1]] = [int]$Matches[2] }
}

$rows = foreach ($id in $ids) {
    $name = if ($names.ContainsKey($id)) { $names[$id] } else { '(not enabled at last start - name unknown)' }
    [pscustomobject]@{ Id = $id; Name = $name; Version = "$($ver[$id])" }
}
$rows = @($rows | Sort-Object Name)

$label = [IO.Path]::GetFileNameWithoutExtension($Save) + ' - mods used by save'
$base = Join-Path $OutDir ($label -replace '[\\/:*?"<>|]', '_')
$utf8 = New-Object Text.UTF8Encoding $false
$rows | Export-Csv "$base.csv" -NoTypeInformation -Encoding UTF8
[IO.File]::WriteAllLines("$base.txt", [string[]]($rows | ForEach-Object { "$($_.Id)`t$($_.Name)" }), $utf8)

$mods = [ordered]@{}; $n = 0
foreach ($r in $rows) {
    $n++
    $mods[$r.Id] = [ordered]@{ Source = ''; Id = $r.Id; Name = $r.Name; Version = $r.Version; LoadOrder = $n
                               IsEnabled = $true; IsVersionLocked = $false; PlaysetId = '' }
}
$playset = [ordered]@{
    ContractFormatVersion = -1
    GeneralData = [ordered]@{ Id = '0'; Name = $label; BannerBytes = $null; Color = $null; Usage = 0 }
    SubscribedMods = $mods; LocalMods = [ordered]@{}
}
[IO.File]::WriteAllText("$base.json", ($playset | ConvertTo-Json -Depth 5 -Compress), $utf8)

Write-Output ("{0} ({1:N0} people): {2} mods used." -f $meta.cityName, $meta.population, $rows.Count)
if ($other.Count) { Write-Output ("Also needs (DLC etc.): " + ($other -join ', ')) }
$unknown = @($rows | Where-Object { $_.Name -like '(not enabled*' }).Count
if ($unknown) { Write-Output "$unknown of them were not enabled at the last game start (names unknown)." }
Write-Output "Written: $base.txt / .csv / .json"
