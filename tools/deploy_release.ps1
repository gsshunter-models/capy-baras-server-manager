param([string]$Version = '1.0.0')
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid version.' }
$taskProject = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskSource = Join-Path $taskProject ('artifacts\CapyBaraServerManager-v' + $Version)
if (-not (Test-Path -LiteralPath (Join-Path $taskSource 'CapyBaraServerManager.exe'))) { throw 'Publish the app first.' }
$taskDesktop = [Environment]::GetFolderPath('Desktop')
$taskTarget = Join-Path $taskDesktop ("Capy Bara's Server Manager v" + $Version)
$taskSuffix = 2
while (Test-Path -LiteralPath $taskTarget) { $taskTarget = Join-Path $taskDesktop ("Capy Bara's Server Manager v" + $Version + ' (' + $taskSuffix + ')'); $taskSuffix++ }
New-Item -ItemType Directory -Path $taskTarget | Out-Null
$taskCount = 0
foreach ($taskFile in Get-ChildItem -LiteralPath $taskSource -File -Recurse) {
    if ($taskFile.Extension -eq '.pdb') { continue }
    $taskDestination = [IO.Path]::GetFullPath((Join-Path $taskTarget ([IO.Path]::GetRelativePath($taskSource, $taskFile.FullName))))
    if (-not $taskDestination.StartsWith($taskTarget + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected deployment path.' }
    New-Item -ItemType Directory -Path (Split-Path $taskDestination) -Force | Out-Null
    Copy-Item -LiteralPath $taskFile.FullName -Destination $taskDestination
    if ((Get-FileHash -LiteralPath $taskDestination).Hash -ne (Get-FileHash -LiteralPath $taskFile.FullName).Hash) { throw 'Copy verification failed.' }
    $taskCount++
}
$taskShortcut = Join-Path $taskDesktop "Capy Bara's Server Manager.lnk"
if (Test-Path -LiteralPath $taskShortcut) { Copy-Item -LiteralPath $taskShortcut -Destination (Join-Path $taskTarget 'Previous version.lnk') }
$taskShell = New-Object -ComObject WScript.Shell
$taskLink = $taskShell.CreateShortcut($taskShortcut)
$taskLink.TargetPath = Join-Path $taskTarget 'CapyBaraServerManager.exe'
$taskLink.WorkingDirectory = $taskTarget
$taskLink.Description = "Capy Bara's Server Manager v" + $Version
$taskLink.IconLocation = $taskLink.TargetPath + ',0'
$taskLink.Save()
[pscustomobject]@{AppFolder=$taskTarget;Shortcut=$taskShortcut;FilesVerified=$taskCount;OriginalServerChanged=$false;NetworkPortsOpened=$false} | ConvertTo-Json | Tee-Object -FilePath (Join-Path $taskProject ('artifacts\deployment-v' + $Version + '.json'))
