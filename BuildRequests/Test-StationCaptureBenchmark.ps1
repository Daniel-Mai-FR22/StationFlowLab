param(
    [Parameter(Mandatory=$true)][string]$Executable,
    [Parameter(Mandatory=$true)][ValidatePattern('^[a-z0-9-]+$')][string]$ReportTag,
    [ValidateRange(20,300)][int]$Seconds=20,
    [ValidateRange(5,120)][int]$Warmup=20,
    [ValidateSet('480','720','HD')][string]$Resolution='720',
    [Alias('NominalPlaybackFps')][ValidateRange(1,50)][int]$Fps=30,
    [int]$Seed=20261020,
    [string]$OperatingDate=(Get-Date -Format 'yyyy-MM-dd'),
    [ValidateSet(1)][int]$SimulationSpeed=1,
    [ValidateSet('default','baseline','common')][string]$Admission='default',
    [ValidateRange(0,4)][int]$ReadbackSlots=0,
    [ValidateSet('default','full','ao-half','ao-off','balanced','copies','lean')][string]$RenderProfile='default',
    [ValidateSet('default','main','background')][string]$DataWriter='default',
    [switch]$WindowedPlayer,
    [ValidateSet('observation','all-off','data-only','video-1','video-2','video-4','video-only-1','video-only-2','video-only-4','video-all','video-only-all')]
    [string[]]$Cases=@('observation','all-off','data-only','video-1','video-2','video-4')
)
$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$playerPath=(Resolve-Path -LiteralPath $Executable).Path
if ([IO.Path]::GetExtension($playerPath) -ne '.exe') { throw 'An explicit Windows Player executable is required.' }
$parsedDate=[DateTime]::MinValue
if (![DateTime]::TryParseExact($OperatingDate,'yyyy-MM-dd',[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::None,[ref]$parsedDate)) { throw 'OperatingDate must be yyyy-MM-dd.' }
if ($DataWriter -ne 'default' -and $Cases -contains 'observation') { throw 'A data writer override requires capture cases; omit observation.' }
$outputRoot=Join-Path $projectRoot ('Reports/TrainStationDaily/PerformanceDiagnosis/Matrix-'+$ReportTag)
$launchRoot=Join-Path $outputRoot 'Unrelated working directory'
[IO.Directory]::CreateDirectory($launchRoot) | Out-Null
$summaryPath=Join-Path $outputRoot 'summary.json'
if (Test-Path -LiteralPath $summaryPath) { throw ('Use a fresh ReportTag; evidence already exists at '+$summaryPath) }
$specs=@{
    'observation'=@('observation',0);'all-off'=@('all-off',0);'data-only'=@('data-only',0)
    'video-1'=@('all-data-video',1);'video-2'=@('all-data-video',2);'video-4'=@('all-data-video',4)
    'video-only-1'=@('video-only',1);'video-only-2'=@('video-only',2);'video-only-4'=@('video-only',4)
    'video-all'=@('all-data-video','all');'video-only-all'=@('video-only','all')
}
$launchMode=if($WindowedPlayer){'Start-Process -WindowStyle Hidden; ordinary Player, -batchmode omitted; 960x540 main game view; runInBackground=true; targetGameFps=60; VSync=0'}else{'Start-Process -WindowStyle Hidden; -batchmode with rendering; 960x540 main game view; runInBackground=true; targetGameFps=60; VSync=0'}
$summary=[ordered]@{
    schema='stationflow.capture_benchmark_matrix.v2';success=$false;startedUtc=[DateTime]::UtcNow.ToString('O');completedUtc=$null
    executable=$playerPath;executableSha256=(Get-FileHash -LiteralPath $playerPath -Algorithm SHA256).Hash.ToLowerInvariant()
    reportTag=$ReportTag;outputRoot=$outputRoot;operatingDate=$OperatingDate;seed=$Seed;resolution=$Resolution;nominalPlaybackFps=$Fps;warmupSeconds=$Warmup;measurementSeconds=$Seconds;simulationSpeed=$SimulationSpeed
    requestedAdmission=$Admission;requestedReadbackSlots=$ReadbackSlots;requestedRenderProfile=$RenderProfile;requestedDataWriter=$DataWriter;requestedWindowedPlayer=[bool]$WindowedPlayer
    primaryMetric='Accepted camera frames / Unity frame delta in the identical capture-clock sync-ID window. Game FPS is a separate visible tradeoff.'
    launchMode=$launchMode
    cases=@();agreement=$null;errors=@()
}
function Save-Summary { $summary | ConvertTo-Json -Depth 50 | Set-Content -LiteralPath $summaryPath -Encoding utf8 }
function Quote-Argument([string]$Value) {
    if ($Value.Contains('"')) { throw 'Embedded argument quotes are unsupported.' }
    '"'+$Value+'"'
}
try {
    foreach ($caseName in $Cases) {
        $spec=$specs[$caseName]
        $isVideo=$spec[0] -in @('all-data-video','video-only')
        $allCameras=[string]$spec[1] -eq 'all'
        $caseRoot=Join-Path $outputRoot $caseName
        [IO.Directory]::CreateDirectory($caseRoot) | Out-Null
        $caseReport=Join-Path $caseRoot 'benchmark.json'
        if (Test-Path -LiteralPath $caseReport) { throw ('Existing case report: '+$caseReport) }
        $logPath=Join-Path $caseRoot 'player.log'
        $arguments=@('--station-capture-benchmark','--station-benchmark-label',$caseName,
            '--station-benchmark-variant',[string]$spec[0],'--station-benchmark-cameras',[string]$spec[1],
            '--station-benchmark-seconds',[string]$Seconds,'--station-benchmark-warmup',[string]$Warmup,
            '--station-benchmark-resolution',$Resolution,'--station-benchmark-fps',[string]$Fps,
            '--station-benchmark-seed',[string]$Seed,'--station-benchmark-date',$OperatingDate,
            '--station-benchmark-sim-speed',[string]$SimulationSpeed,'--station-benchmark-output',$caseRoot,
            '-screen-fullscreen','0','-screen-width','960','-screen-height','540','-logFile',$logPath)
        if (!$WindowedPlayer) { $arguments+=@('-batchmode') }
        # Omitted overrides preserve the Player's production defaults and old Player compatibility.
        if ($isVideo) {
            if ($Admission -ne 'default') { $arguments+=@('--station-benchmark-admission',$Admission) }
            if ($ReadbackSlots -ne 0) { $arguments+=@('--station-benchmark-readback-slots',[string]$ReadbackSlots) }
            if ($RenderProfile -ne 'default') { $arguments+=@('--station-benchmark-render-profile',$RenderProfile) }
        }
        if ($DataWriter -ne 'default') { $arguments+=@('--station-benchmark-data-writer',$DataWriter) }
        $argumentString=($arguments | ForEach-Object { Quote-Argument $_ }) -join ' '
        $began=[DateTime]::UtcNow
        $player=Start-Process -FilePath $playerPath -ArgumentList $argumentString -WorkingDirectory $launchRoot -WindowStyle Hidden -PassThru
        Write-Output ('Started '+$caseName+' PID '+$player.Id+'; warmup '+$Warmup+'s, measured '+$Seconds+'s.')
        $timeoutSeconds=$Warmup+$Seconds+180
        $timedOut=$false
        while (!$player.WaitForExit(1000)) {
            if (([DateTime]::UtcNow-$began).TotalSeconds -gt $timeoutSeconds) {
                $timedOut=$true
                Stop-Process -Id $player.Id -ErrorAction SilentlyContinue
                break
            }
        }
        $player.Refresh()
        $exitCode=if($timedOut){-1}else{$player.ExitCode}
        $report=$null
        if (Test-Path -LiteralPath $caseReport) { $report=Get-Content -LiteralPath $caseReport -Raw -Encoding utf8 | ConvertFrom-Json }
        $resolvedCount=if($report -and $null -ne $report.resolvedCameraCount){[int]$report.resolvedCameraCount}elseif($allCameras){$null}else{[int]$spec[1]}
        $entry=[ordered]@{name=$caseName;variant=$spec[0];cameraCount=$resolvedCount;requestedCameraSelection=[string]$spec[1];exitCode=$exitCode;timedOut=$timedOut;reportPath=$caseReport;logPath=$logPath;report=$report}
        $summary.cases+=,[pscustomobject]$entry
        Save-Summary
        if ($timedOut -or $exitCode -ne 0 -or !$report -or !$report.success) { throw ('Benchmark case failed: '+$caseName+'. Inspect '+$caseReport+' and '+$logPath) }
        if ([bool]$report.batchMode -eq [bool]$WindowedPlayer) { throw ('Observed Player batch mode differs from requested launch context: '+$caseName) }
        if (!$report.fixedSeedVerified -or $report.seed -ne $Seed -or $report.operatingDate -ne $OperatingDate) { throw ('Seed/date mismatch: '+$caseName) }
        if ($report.warmupRealSeconds -lt $Warmup -or $report.measuredRealSeconds -lt $Seconds) { throw ('Incomplete timing window: '+$caseName) }
        if ($DataWriter -ne 'default' -and $report.resolvedDataWriterMode -ne $DataWriter) { throw ('Data writer override did not bind: '+$caseName) }
        if ($allCameras) {
            if ($report.resolvedCameraSelection -ne 'all' -or $report.installedCameraCount -lt 1 -or $report.resolvedCameraCount -ne $report.installedCameraCount -or $report.finalizedCameraCount -ne $report.installedCameraCount -or !$report.finalizedCameraSetVerified) { throw ('All-camera inventory/count verification failed: '+$caseName) }
            if (@(Compare-Object @($report.installedCameraIds) @($report.resolvedCameraIds) -CaseSensitive).Count -gt 0 -or @(Compare-Object @($report.resolvedCameraIds) @($report.finalizedCameraIds) -CaseSensitive).Count -gt 0) { throw ('All-camera ID sets differ: '+$caseName) }
            Write-Output ('All installed cameras verified: '+$report.finalizedCameraCount+'; '+($report.finalizedCameraIds -join ', '))
        }
        if ($isVideo) {
            if ($Admission -ne 'default' -and $report.resolvedAdmission -ne $Admission) { throw ('Admission override did not bind: '+$caseName) }
            if ($ReadbackSlots -ne 0 -and $report.resolvedReadbackSlots -ne $ReadbackSlots) { throw ('Readback slot override did not bind: '+$caseName) }
            if ($RenderProfile -ne 'default' -and $report.resolvedRenderProfile -ne $RenderProfile) { throw ('Render profile override did not bind: '+$caseName) }
        }
        if ($report.resolvedDataWriterMode) { Write-Output ('Data writer '+$report.resolvedDataWriterMode+'; actual rendering threading mode '+$report.hardware.renderingThreadingMode+'.') }
        Write-Output ('Completed '+$caseName+': game '+[Math]::Round($report.gameFps,2)+' FPS; video '+(($report.cameras | ForEach-Object {[Math]::Round($_.acceptedFps,2)}) -join ', ')+' FPS/camera.')
        if ($report.schema -eq 'stationflow.capture_benchmark.v2' -and $isVideo) {
            Write-Output ('Camera/game frame coverage '+[Math]::Round(100*$report.cameraGameFrameRatioMin,2)+'–'+[Math]::Round(100*$report.cameraGameFrameRatioMax,2)+'%; admission '+$report.resolvedAdmission+', slots '+$report.resolvedReadbackSlots+', rendering '+$report.resolvedRenderProfile+'.')
        }
    }
    $reports=@($summary.cases | ForEach-Object {$_.report})
    $scheduleHashes=@($reports.timetableSha256 | Select-Object -Unique)
    $arrivalHashes=@($reports.arrivalPlanSha256 | Select-Object -Unique)
    $buildGuids=@($reports.applicationBuildGuid | Select-Object -Unique)
    $scenes=@($reports.scene | Select-Object -Unique)
    $startTimes=$reports.startSimulationSeconds | Measure-Object -Minimum -Maximum
    $endTimes=$reports.endSimulationSeconds | Measure-Object -Minimum -Maximum
    $summary.agreement=[ordered]@{
        identicalTimetables=$scheduleHashes.Count -eq 1;identicalArrivalPlans=$arrivalHashes.Count -eq 1;identicalBuild=$buildGuids.Count -eq 1;identicalScene=$scenes.Count -eq 1
        timetableHashes=$scheduleHashes;arrivalPlanHashes=$arrivalHashes;buildGuids=$buildGuids;scenes=$scenes
        observedSimulationStartRangeSeconds=$startTimes.Maximum-$startTimes.Minimum
        observedSimulationEndRangeSeconds=$endTimes.Maximum-$endTimes.Minimum
        note='Realtime actor samples disclose any workload drift. Identical seeds and source plans do not guarantee bitwise-identical native crowd positions.'
    }
    $summary.success=$summary.agreement.identicalTimetables -and $summary.agreement.identicalArrivalPlans -and $summary.agreement.identicalBuild -and $summary.agreement.identicalScene
    if (!$summary.success) { throw 'Benchmark source plans/build/scene do not match across cases.' }
} catch {
    $summary.errors+=,$_.Exception.ToString()
    throw
} finally {
    $summary.completedUtc=[DateTime]::UtcNow.ToString('O')
    Save-Summary
    Write-Output ('Benchmark matrix evidence: '+$summaryPath)
}
