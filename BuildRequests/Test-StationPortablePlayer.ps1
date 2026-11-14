param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [Parameter(Mandatory = $true)][string]$ReportTag,
    [ValidateSet("480","720","HD")][string]$Resolution = "480"
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$buildRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'Builds'))
$playerPath = (Resolve-Path -LiteralPath $Executable).Path
$desktopRoot = [Environment]::GetFolderPath('DesktopDirectory')
$playerDirectoryCandidate = [IO.Path]::GetDirectoryName($playerPath)
$isProjectBuild = $playerPath.StartsWith($buildRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
$isDesktopRelease = [string]::Equals([IO.Path]::GetDirectoryName($playerDirectoryCandidate), $desktopRoot, [StringComparison]::OrdinalIgnoreCase) -and
    [IO.Path]::GetFileName($playerDirectoryCandidate) -match '^GareFlowDemo_v[0-9]+\.[0-9]+\.[0-9]+_b[0-9]+$' -and
    [IO.Path]::GetFileName($playerPath) -eq 'GareFlowDemo.exe'
if (!$isProjectBuild -and !$isDesktopRelease) {
    throw 'The verification player must be a project build or an explicitly versioned GareFlowDemo Desktop release.'
}
if ($ReportTag -notmatch '^[a-z0-9-]+$') { throw 'Invalid report tag.' }
$evidenceRoot = Join-Path $projectRoot 'Reports/TrainStationDaily/RuntimeInitialization'
$launchDirectory = Join-Path $evidenceRoot 'Unrelated working directory'
[IO.Directory]::CreateDirectory($launchDirectory) | Out-Null
$logPath = Join-Path $evidenceRoot ($ReportTag + '-player.log')
$arguments = '--station-collection-smoke --station-smoke-resolution ' + $Resolution + ' -batchmode -screen-fullscreen 0 -screen-width 960 -screen-height 540 -logFile "' + $logPath + '"'
$startedUtc = [DateTime]::UtcNow.ToString('O')
$playerProcess = Start-Process -FilePath $playerPath -ArgumentList $arguments -WorkingDirectory $launchDirectory -WindowStyle Hidden -PassThru
Write-Output ('Started explicit verification player PID ' + $playerProcess.Id)
if (!$playerProcess.WaitForExit(150000)) {
    Stop-Process -Id $playerProcess.Id -ErrorAction SilentlyContinue
    throw 'The explicit portable verification player exceeded 150 seconds.'
}
$playerProcess.Refresh()
$exitCode = $playerProcess.ExitCode
$playerDirectory = [IO.Path]::GetDirectoryName($playerPath)
$expectedStorage = Join-Path $playerDirectory 'Captures/TrainStationDaily'
$reportPath = Join-Path $expectedStorage 'portable-smoke-report.json'
if (!(Test-Path -LiteralPath $reportPath)) { throw ('Player report missing. Inspect ' + $logPath) }
$report = Get-Content -LiteralPath $reportPath -Raw -Encoding utf8 | ConvertFrom-Json
$evidence = [ordered]@{
    success = $false
    startedUtc = $startedUtc
    completedUtc = [DateTime]::UtcNow.ToString('O')
    executable = $playerPath
    launchWorkingDirectory = $launchDirectory
    requestedResolution = $Resolution
    captureCadence = "each_unity_frame"
    exitCode = $exitCode
    reportPath = $reportPath
    logPath = $logPath
    expectedStorage = $expectedStorage
    actualWorkingDirectory = $report.workingDirectory
    actualStorage = $report.storageRoot
    captureDirectory = $report.captureDirectory
    wrongWorkingDirectoryCaptureAbsent = !(Test-Path -LiteralPath (Join-Path $launchDirectory 'Captures'))
    playerReport = $report
}
$evidence.success = $exitCode -eq 0 -and $report.success -and $evidence.wrongWorkingDirectoryCaptureAbsent -and
    [string]::Equals($expectedStorage, $report.storageRoot, [StringComparison]::OrdinalIgnoreCase) -and
    [string]::Equals($launchDirectory, $report.workingDirectory, [StringComparison]::OrdinalIgnoreCase)
$evidencePath = Join-Path $evidenceRoot ($ReportTag + '-verification.json')
$evidence | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $evidencePath -Encoding utf8
[pscustomobject]$evidence | Select-Object success,exitCode,executable,actualWorkingDirectory,actualStorage,captureDirectory | ConvertTo-Json
if (!$evidence.success) { throw ('Portable player verification failed. Inspect ' + $evidencePath) }
