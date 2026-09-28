<#
.SYNOPSIS
  Builds a minimal Skyve playset for one save: only the mods the save needs, plus what those mods require.

.DESCRIPTION
  Read-only for the game; writes "<save> test playset.json" (Skyve playset export format) and a .txt list to the Desktop.
  * Needed mods: the save's "contentPrerequisites" — exactly the list the game checks for its missing-content warning.
  * Plus requirements: every non-optional mod those mods require on Paradox Mods (and what those require, and so on),
    read from Skyve's own mod cache (ModsData\Skyve\PdxModsCache.json).
  * Plus -Keep: IDs of code mods you want anyway (tools such as Move It), and Performance Detective is local and always loads.
  Code mods that store their own data in the save (for example Traffic's lane connections) are not recorded by the save;
  without them the city loads, but that data is dropped if you save — so use the test playset for testing, and save the
  city only with your full playset, or add those mods with -Keep.

.EXAMPLE
  .\TestPlayset.ps1 -Save "...\Saves\76561197960271872\Starford 2.cok" -Keep 74604,75250
#>
param(
    [Parameter(Mandatory)] [string]$Save,
    [string[]]$Keep = @(),
    [string]$UserData = (Join-Path $env:USERPROFILE 'AppData\LocalLow\Colossal Order\Cities Skylines II'),
    [string]$OutDir = [Environment]::GetFolderPath('Desktop')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

# --- What the save needs ---
$zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path $Save).Path)
try {
    $entry = $zip.Entries | Where-Object { $_.FullName -like '*.SaveGameMetadata' } | Select-Object -First 1
    if (-not $entry) { throw "$Save has no save metadata" }
    $reader = New-Object IO.StreamReader($entry.Open())
    try { $meta = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Close() }
} finally { $zip.Dispose() }
$needed = New-Object System.Collections.Generic.List[string]
$dlc = @()
foreach ($p in @($meta.contentPrerequisites)) { if ("$p" -match '^Mod:(\d+)$') { $needed.Add($Matches[1]) } else { $dlc += "$p" } }

# --- Mod names, versions and requirements from Skyve's cache (newest entry per mod) ---
$cacheFile = Join-Path $UserData 'ModsData\Skyve\PdxModsCache.json'
$info = @{}
if (Test-Path $cacheFile) {
    # Parsed as dictionaries: Windows PowerShell's ConvertFrom-Json rejects some keys in this file.
    $text = [IO.File]::ReadAllText($cacheFile)
    if ($PSVersionTable.PSVersion.Major -ge 7) { $cache = $text | ConvertFrom-Json -AsHashtable }
    else {
        Add-Type -AssemblyName System.Web.Extensions
        $ser = New-Object System.Web.Script.Serialization.JavaScriptSerializer
        $ser.MaxJsonLength = [int]::MaxValue
        $cache = $ser.DeserializeObject($text)
    }
    foreach ($m in $cache.Values) {
        if (-not ($m -is [System.Collections.IDictionary]) -or -not $m['Id']) { continue }
        $id = "$($m['Id'])"
        $v = 0; [void][int]::TryParse("$($m['Version'])", [ref]$v)
        if (-not $info.ContainsKey($id) -or $info[$id].Ver -lt $v) {
            $req = @(@($m['Requirements']) | Where-Object { $_ -is [System.Collections.IDictionary] -and -not $_['IsDlc'] -and -not $_['IsOptional'] -and $_['Id'] } | ForEach-Object { "$($_['Id'])" })
            $info[$id] = @{ Ver = $v; Name = "$($m['Name'])"; Req = $req }
        }
    }
} else { Write-Warning "Skyve's mod cache was not found; requirements are not added." }

# Enabled mods at the last game start (names, and to flag mods the save needs that are not enabled).
$enabledNames = @{}
$fs = [IO.File]::Open((Join-Path $UserData 'Logs\Modding.log'), 'Open', 'Read', 'ReadWrite, Delete')
$logReader = New-Object IO.StreamReader($fs)
while ($null -ne ($line = $logReader.ReadLine())) {
    if ($line -match '^\s*- (.+?) \((\d+)\)\s*$') { $enabledNames[$Matches[2]] = ($Matches[1] -replace ' v[\w\.\-\+]*$', '') }
}
$logReader.Close()

# --- Needed + requirements (transitively) + kept ---
$reason = [ordered]@{}
foreach ($id in $needed) { if (-not $reason.Contains($id)) { $reason[$id] = 'used by the save' } }
foreach ($id in $Keep) { if (-not $reason.Contains("$id")) { $reason["$id"] = 'kept (-Keep)' } }
$queue = New-Object System.Collections.Generic.Queue[string]
foreach ($id in @($reason.Keys)) { $queue.Enqueue($id) }
while ($queue.Count -gt 0) {
    $id = $queue.Dequeue()
    if (-not $info.ContainsKey($id)) { continue }
    foreach ($r in $info[$id].Req) {
        if ($reason.Contains($r)) { continue }
        $reason[$r] = 'required by ' + $(if ($info.ContainsKey($id)) { $info[$id].Name } else { $id })
        $queue.Enqueue($r)
    }
}

$rows = foreach ($id in $reason.Keys) {
    $name = if ($enabledNames.ContainsKey($id)) { $enabledNames[$id] } elseif ($info.ContainsKey($id)) { $info[$id].Name } else { "(unknown mod $id)" }
    $ver = if ($info.ContainsKey($id)) { "$($info[$id].Ver)" } else { '' }
    [pscustomobject]@{ Id = $id; Name = $name; Version = $ver; Why = $reason[$id]; EnabledNow = $enabledNames.ContainsKey($id) }
}
$rows = @($rows | Sort-Object @{ Expression = { $_.Why -ne 'used by the save' } }, Name)

# --- Write ---
$label = [IO.Path]::GetFileNameWithoutExtension($Save) + ' test playset'
$base = Join-Path $OutDir ($label -replace '[\\/:*?"<>|]', '_')
$utf8 = New-Object Text.UTF8Encoding $false
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
[IO.File]::WriteAllLines("$base.txt", [string[]]($rows | ForEach-Object { "$($_.Id)`t$($_.Name)`t$($_.Why)" }), $utf8)

$byWhy = $rows | Group-Object { if ($_.Why -like 'required by*') { 'required' } else { $_.Why } }
Write-Output ("{0} ({1:N0} people): {2} mods in the test playset ({3})." -f $meta.cityName, $meta.population, $rows.Count,
    (($byWhy | ForEach-Object { "$($_.Count) $($_.Name)" }) -join ', '))
$notEnabled = @($rows | Where-Object { -not $_.EnabledNow })
if ($notEnabled.Count) { Write-Output ("Not enabled at your last game start (Skyve will enable or subscribe them): " + (($notEnabled | ForEach-Object { "$($_.Name) ($($_.Id))" }) -join ', ')) }
if ($dlc.Count) { Write-Output ("DLC the save also needs: " + ($dlc -join ', ')) }
Write-Output "Written: $base.json (import in Skyve) and $base.txt"
$rows | Where-Object { $_.Why -ne 'used by the save' } | Select-Object Id, Name, Why | Format-Table -AutoSize | Out-String -Width 200
