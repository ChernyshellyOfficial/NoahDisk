# Обновляет встроенный список имён программ (gui/program_names.txt.gz).
# Тянет популярные пакеты из community.chocolatey.org (по числу загрузок), чистит от
# шума (обновления Windows, распространяемые компоненты, инфраструктура choco, драйверы)
# и пакует «сырые» имена — приложение нормализует их при загрузке той же функцией, что и
# имена папок. Запуск:  powershell -File tools\update-programdb.ps1
$ErrorActionPreference = 'Stop'

$raw = New-Object System.Collections.Generic.List[string]
for ($skip = 0; $skip -lt 2500; $skip += 30) {
  $u = "https://community.chocolatey.org/api/v2/Packages()?`$filter=IsLatestVersion&`$orderby=DownloadCount%20desc&`$skip=$skip&`$top=30&`$select=Title"
  try { $r = Invoke-WebRequest $u -TimeoutSec 30 -UseBasicParsing } catch { break }
  $m = ([regex]'<d:Title>(.*?)</d:Title>').Matches($r.Content)
  if ($m.Count -eq 0) { break }
  foreach ($x in $m) { $raw.Add([System.Net.WebUtility]::HtmlDecode($x.Groups[1].Value)) }
}
Write-Host "raw titles: $($raw.Count)"

$noise = 'redistributable|visual c\+\+|\.net framework|silverlight|hotfix|servicing|security (update|advisory)|update for|\bKB\d{4,}|language pack|universal c runtime|^chocolatey|chocolatey.*extension|\bextension$|deprecated|service pack|directx|webview2|\bdriver\b|build tools|deployment|management objects|management framework|\bSDK\b|shared management|advisory|runtime and shared|asp\.net|dsc modules|express with|with sp\d|community dsc|\.net core|tools root'

$seen = New-Object System.Collections.Generic.HashSet[string]
$names = New-Object System.Collections.Generic.List[string]
foreach ($t in $raw) {
  $name = ($t -split ':')[0]
  $name = $name -replace '\([^)]*\)','' -replace '\[[^\]]*\]',''
  $name = $name -replace '\s[-|]\s.*$',''
  $name = ($name -replace '\s+',' ').Trim()
  if ($name.Length -eq 0 -or $t -match $noise -or $name -match $noise) { continue }
  $norm = (($name.ToCharArray() | Where-Object { [char]::IsLetterOrDigit($_) }) -join '').ToLowerInvariant()
  if ($norm.Length -ge 8 -and $seen.Add($norm)) { $names.Add($name) }
}
$names = $names | Sort-Object
Write-Host "clean names: $($names.Count)"

$gzPath = Join-Path $PSScriptRoot '..\gui\program_names.txt.gz'
$bytes = [System.Text.Encoding]::UTF8.GetBytes(($names -join "`n") + "`n")
$fs = [System.IO.File]::Create($gzPath)
$gz = New-Object System.IO.Compression.GZipStream($fs, [System.IO.Compression.CompressionLevel]::Optimal)
$gz.Write($bytes, 0, $bytes.Length); $gz.Close(); $fs.Close()
Write-Host "wrote $([System.IO.Path]::GetFullPath($gzPath)) ($([math]::Round((Get-Item $gzPath).Length/1KB,1)) KB)"
