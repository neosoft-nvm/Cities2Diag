<#
.SYNOPSIS
  Writes a Skyve playset (.json, Skyve's export format) for one save: your currently enabled mods minus the asset packs
  that save does not use.

.DESCRIPTION
  Read-only for the game: runs SaveInspector.ps1 for the save, then writes "<City> lean playset.json" to the Desktop.
  Kept: every mod the save uses, every code mod (saves do not record code mods), and asset packs smaller than
  -KeepBelowMB (highway lanes, interchanges, dependencies: they save nothing and roads do not always appear in a save's
  list). Import the file in Skyve (Playsets → Import), activate it and start the game from Skyve.

.EXAMPLE
  .\LeanPlayset.ps1 -Save "$env:USERPROFILE\AppData\LocalLow\Colossal Order\Cities Skylines II\Saves\76561197960271872\Starford 2.cok"
#>
param(
    [Parameter(Mandatory)] [string]$Save,
    [string]$Name,
    [double]$KeepBelowMB = 2,
    [string]$UserData = (Join-Path $env:USERPROFILE 'AppData\LocalLow\Colossal Order\Cities Skylines II'),
    [string]$OutDir = [Environment]::GetFolderPath('Desktop')
)
$ErrorActionPreference = 'Stop'

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("cs2_lean_" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $tmp | Out-Null
try {
    $summary = & (Join-Path $PSScriptRoot 'SaveInspector.ps1') -Save $Save -UserData $UserData -OutDir $tmp
    $csvFile = Get-ChildItem $tmp -Filter 'cs2_save_mods_*.csv' | Select-Object -First 1
    $rows = @{}
    foreach ($r in Import-Csv $csvFile.FullName) { $rows[$r.Id] = $r }
    $city = ($summary | Where-Object { $_ -like 'Save: *' } | Select-Object -First 1) -replace '^Save: (.+?) \(.*$', '$1'
} finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }
if (-not $Name) { $Name = "$city lean" }

# Newest cached version per mod, and the enabled mods in load order (last game start).
$ver = @{}
foreach ($d in Get-ChildItem (Join-Path $UserData '.cache\Mods\pdx_mods') -Directory) {
    if ($d.Name -match '^(\d+)_(\d+)$' -and (-not $ver.ContainsKey($Matches[1]) -or $ver[$Matches[1]] -lt [int]$Matches[2])) { $ver[$Matches[1]] = [int]$Matches[2] }
}
$fs = [IO.File]::Open((Join-Path $UserData 'Logs\Modding.log'), 'Open', 'Read', 'ReadWrite, Delete')
$reader = New-Object IO.StreamReader($fs)
$order = New-Object System.Collections.Generic.List[object]
while ($null -ne ($line = $reader.ReadLine())) { if ($line -match '^\s*- (.+?) \((\d+)\)\s*$') { $order.Add(@($Matches[2], $Matches[1])) } }
$reader.Close()

$mods = [ordered]@{}; $n = 0; $dropped = @(); $tiny = 0
foreach ($o in $order) {
    $id = $o[0]; $r = $rows[$id]
    if ($r -and $r.Status -eq 'NOT USED by this save') {
        if ([double]$r.AssetMB -ge $KeepBelowMB) { $dropped += $r; continue }
        $tiny++
    }
    $n++
    $mods[$id] = [ordered]@{ Source = ''; Id = $id; Name = ($o[1] -replace ' v[\w\.\-]*$', ''); Version = "$($ver[$id])"; LoadOrder = $n
                             IsEnabled = $true; IsVersionLocked = $false; PlaysetId = '' }
}
$playset = [ordered]@{
    ContractFormatVersion = -1
    GeneralData = [ordered]@{ Id = '0'; Name = $Name; BannerBytes = $null; Color = $null; Usage = 0 }
    SubscribedMods = $mods; LocalMods = [ordered]@{}
}
$droppedMb = 0; foreach ($d in $dropped) { $droppedMb += [double]$d.AssetMB }
$dropped = @($dropped | Sort-Object { [double]$_.AssetMB } -Descending)
$out = Join-Path $OutDir (($Name -replace '[\\/:*?"<>|]', '_') + ' playset.json')
[IO.File]::WriteAllText($out, ($playset | ConvertTo-Json -Depth 5 -Compress), (New-Object Text.UTF8Encoding $false))

Write-Output ("{0}: {1} mods kept ({2} small packs under {3} MB kept for safety); {4} asset packs left out, {5:N0} MB." -f $city, $n, $tiny, $KeepBelowMB, $dropped.Count, $droppedMb)
Write-Output "Playset: $out"
$dropped | Select-Object Id, Name, AssetMB, AlsoUsedBy | Format-Table -AutoSize -Wrap | Out-String -Width 200
