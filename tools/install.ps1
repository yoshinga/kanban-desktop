param(
  [switch]$NoPublish
)
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$appDir = Join-Path $env:LOCALAPPDATA "Kanban\app"
$exe = Join-Path $appDir "Kanban.Desktop.exe"

if (-not $NoPublish) {
  Write-Output "publishing to $appDir ..."
  dotnet publish (Join-Path $root "src\Kanban.Desktop\Kanban.Desktop.csproj") -c Release -o $appDir | Out-Null
}

if (-not (Test-Path $exe)) { throw "exe not found: $exe" }

function New-Lnk([string]$linkPath, [string]$target, [string]$workdir, [string]$desc) {
  $ws = New-Object -ComObject WScript.Shell
  $lnk = $ws.CreateShortcut($linkPath)
  $lnk.TargetPath = $target
  $lnk.WorkingDirectory = $workdir
  $lnk.IconLocation = "$target,0"
  $lnk.Description = $desc
  $lnk.Save()
}

$desktop = [Environment]::GetFolderPath("Desktop")
$deskLink = Join-Path $desktop "Kanban.lnk"
New-Lnk $deskLink $exe $appDir "Kanban (Dropbox)"

$programs = [Environment]::GetFolderPath("Programs")
$startLink = Join-Path $programs "Kanban.lnk"
New-Lnk $startLink $exe $appDir "Kanban (Dropbox)"

Write-Output "desktop shortcut: $deskLink"
Write-Output "start menu shortcut: $startLink"
Write-Output "exe: $exe"
