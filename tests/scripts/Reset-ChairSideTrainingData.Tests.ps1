[CmdletBinding()]
param()

Set-StrictMode -Version 2.0
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$wrapperPath = Join-Path $repositoryRoot "scripts/Reset-ChairSideTrainingData.ps1"
$wrapperSource = Get-Content -Raw -LiteralPath $wrapperPath

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) {
        throw $Message
    }
}

function Assert-Contains {
    param([string]$Actual, [string]$Expected, [string]$Label)
    if ($Actual.IndexOf($Expected, [System.StringComparison]::Ordinal) -lt 0) {
        throw "$Label did not contain '$Expected'. Actual output:`n$Actual"
    }
}

function Assert-ContainsIgnoringWhitespace {
    param([string]$Actual, [string]$Expected, [string]$Label)

    $normalizedActual = [regex]::Replace($Actual, '\s+', '')
    $normalizedExpected = [regex]::Replace($Expected, '\s+', '')

    if ($normalizedActual.IndexOf(
        $normalizedExpected,
        [System.StringComparison]::Ordinal
    ) -lt 0) {
        throw "$Label did not contain '$Expected' when whitespace was ignored. Actual output:`n$Actual"
    }
}

function Assert-NotContains {
    param([string]$Actual, [string]$Unexpected, [string]$Label)
    if ($Actual.IndexOf($Unexpected, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "$Label unexpectedly contained '$Unexpected'. Actual output:`n$Actual"
    }
}

function Invoke-Wrapper {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    $stdoutPath = [System.IO.Path]::GetTempFileName()
    $stderrPath = [System.IO.Path]::GetTempFileName()

    try {
        $quotedWrapperPath = '"' + $wrapperPath + '"'
        $processArguments = @(
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            $quotedWrapperPath
        ) + $Arguments

        $process = Start-Process `
            -FilePath "powershell.exe" `
            -ArgumentList $processArguments `
            -Wait `
            -PassThru `
            -NoNewWindow `
            -RedirectStandardOutput $stdoutPath `
            -RedirectStandardError $stderrPath

        $standardOutput = Get-Content -Raw -LiteralPath $stdoutPath
        $standardError = Get-Content -Raw -LiteralPath $stderrPath
        if ($null -eq $standardOutput) {
            $standardOutput = ""
        }
        if ($null -eq $standardError) {
            $standardError = ""
        }

        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            Output = @(
                $standardOutput
                $standardError
            ) -join [Environment]::NewLine
        }
    }
    finally {
        Remove-Item -LiteralPath $stdoutPath, $stderrPath -Force -ErrorAction SilentlyContinue
    }
}

function Assert-SuccessfulPlan {
    param(
        [Parameter(Mandatory = $true)]$Result,
        [Parameter(Mandatory = $true)][string]$Mode,
        [Parameter(Mandatory = $true)][string]$Token,
        [Parameter(Mandatory = $true)][string]$ExpectedArguments
    )

    Assert-True ($Result.ExitCode -eq 0) "$Mode -WhatIf exited with $($Result.ExitCode):`n$($Result.Output)"
    Assert-Contains $Result.Output "Mode:              $Mode" "$Mode plan"
    Assert-Contains $Result.Output "App root:          C:\ChairSide\Training\App" "$Mode plan"
    Assert-Contains $Result.Output "App DLL:           C:\ChairSide\Training\App\ChairSide.Board.dll" "$Mode plan"
    Assert-Contains $Result.Output "Data root:         C:\ChairSide\Training\Data" "$Mode plan"
    Assert-Contains $Result.Output "Database:          C:\ChairSide\Training\Data\chairside-training.db" "$Mode plan"
    Assert-Contains $Result.Output "Backup root:       C:\ChairSide\Training\Backups" "$Mode plan"
    Assert-Contains $Result.Output "App pool:          ChairSideBoard-Training" "$Mode plan"
    Assert-Contains $Result.Output "Child environment: Training" "$Mode plan"
    Assert-Contains $Result.Output "Required token:    $Token" "$Mode plan"
    Assert-Contains $Result.Output $ExpectedArguments "$Mode CLI plan"
    Assert-Contains $Result.Output "WhatIf completed. No IIS, filesystem, or application action was performed." "$Mode plan"
}

$clean = Invoke-Wrapper @(
    "-Mode", "Clean",
    "-ConfirmationToken", "RESET_EMPTY_BETA",
    "-WhatIf"
)
Assert-SuccessfulPlan $clean "Clean" "RESET_EMPTY_BETA" "--maintenance reset-empty --confirm RESET_EMPTY_BETA"

$trainingSeed = Invoke-Wrapper @(
    "-Mode", "TrainingSeed",
    "-ConfirmationToken", "RESET_TRAINING_DATA",
    "-WhatIf"
)
Assert-SuccessfulPlan $trainingSeed "TrainingSeed" "RESET_TRAINING_DATA" "--maintenance reset-training-data --confirm RESET_TRAINING_DATA"

$fullStress = Invoke-Wrapper @(
    "-Mode", "FullStress",
    "-ConfirmationToken", "RESET_STRESS_FIXTURE",
    "-WhatIf"
)
Assert-SuccessfulPlan $fullStress "FullStress" "RESET_STRESS_FIXTURE" "--maintenance reset-stress-fixture --confirm RESET_STRESS_FIXTURE --profile full-stress"

$reportingVolume = Invoke-Wrapper @(
    "-Mode", "ReportingVolume",
    "-ConfirmationToken", "RESET_STRESS_FIXTURE",
    "-CompletedCycles", "500",
    "-WhatIf"
)
Assert-SuccessfulPlan $reportingVolume "ReportingVolume" "RESET_STRESS_FIXTURE" "--maintenance reset-stress-fixture --confirm RESET_STRESS_FIXTURE --profile reporting-volume --completed-cycles 500"

$reportingVolumeDefault = Invoke-Wrapper @(
    "-Mode", "ReportingVolume",
    "-ConfirmationToken", "RESET_STRESS_FIXTURE",
    "-WhatIf"
)
Assert-SuccessfulPlan $reportingVolumeDefault "ReportingVolume" "RESET_STRESS_FIXTURE" "--maintenance reset-stress-fixture --confirm RESET_STRESS_FIXTURE --profile reporting-volume"
Assert-NotContains $reportingVolumeDefault.Output "--completed-cycles" "ReportingVolume default plan"

$wrongToken = Invoke-Wrapper @(
    "-Mode", "FullStress",
    "-ConfirmationToken", "WRONG_TOKEN",
    "-WhatIf"
)
Assert-True ($wrongToken.ExitCode -ne 0) "Wrong token unexpectedly succeeded."
Assert-ContainsIgnoringWhitespace $wrongToken.Output "Confirmation token does not match. Mode 'FullStress' requires -ConfirmationToken RESET_STRESS_FIXTURE. No action was taken." "Wrong-token refusal"
foreach ($unexpected in @("Mode:              FullStress", "Stopping IIS", "Backup created", "CLI command:")) {
    Assert-NotContains $wrongToken.Output $unexpected "Wrong-token refusal"
}

foreach ($mode in @("Clean", "TrainingSeed", "FullStress")) {
    $token = if ($mode -eq "Clean") { "RESET_EMPTY_BETA" } elseif ($mode -eq "TrainingSeed") { "RESET_TRAINING_DATA" } else { "RESET_STRESS_FIXTURE" }
    $result = Invoke-Wrapper @(
        "-Mode", $mode,
        "-ConfirmationToken", $token,
        "-CompletedCycles", "500",
        "-WhatIf"
    )
    Assert-True ($result.ExitCode -ne 0) "$mode unexpectedly accepted -CompletedCycles."
    Assert-ContainsIgnoringWhitespace $result.Output "-CompletedCycles is valid only with -Mode ReportingVolume" "$mode completed-cycle refusal"
}

foreach ($count in @(99, 10001)) {
    $result = Invoke-Wrapper @(
        "-Mode", "ReportingVolume",
        "-ConfirmationToken", "RESET_STRESS_FIXTURE",
        "-CompletedCycles", $count.ToString(),
        "-WhatIf"
    )
    Assert-True ($result.ExitCode -ne 0) "ReportingVolume unexpectedly accepted completed-cycle count $count."
}

foreach ($count in @(100, 10000)) {
    $result = Invoke-Wrapper @(
        "-Mode", "ReportingVolume",
        "-ConfirmationToken", "RESET_STRESS_FIXTURE",
        "-CompletedCycles", $count.ToString(),
        "-WhatIf"
    )
    Assert-True ($result.ExitCode -eq 0) "ReportingVolume rejected valid completed-cycle count $count."
    Assert-Contains $result.Output "--completed-cycles $count" "ReportingVolume boundary plan"
}

foreach ($removedParameter in @("AppPath", "DatabasePath", "BackupRoot", "AppPoolName", "Environment")) {
    $result = Invoke-Wrapper @(
        "-Mode", "Clean",
        "-ConfirmationToken", "RESET_EMPTY_BETA",
        "-$removedParameter", "not-allowed",
        "-WhatIf"
    )
    Assert-True ($result.ExitCode -ne 0) "Removed parameter -$removedParameter was unexpectedly accepted."
}

foreach ($canonicalValue in @(
    "C:\ChairSide\Training\App",
    "C:\ChairSide\Training\App\ChairSide.Board.dll",
    "C:\ChairSide\Training\Data",
    "C:\ChairSide\Training\Data\chairside-training.db",
    "C:\ChairSide\Training\Backups",
    "ChairSideBoard-Training",
    '$env:ASPNETCORE_ENVIRONMENT = $trainingEnvironment'
)) {
    Assert-Contains $wrapperSource $canonicalValue "Wrapper source"
}

foreach ($forbiddenValue in @(
    "C:\ChairSide\App",
    "C:\ChairSide\Data\chairside.db",
    "C:\ChairSide\Backups",
    '$env:ASPNETCORE_ENVIRONMENT = "Production"'
)) {
    Assert-NotContains $wrapperSource $forbiddenValue "Wrapper source"
}

$initialStateQueryIndex = $wrapperSource.IndexOf('$initialAppPoolState = Get-AppPoolState -Name $trainingAppPoolName', [System.StringComparison]::Ordinal)
$startedBranchIndex = $wrapperSource.IndexOf('if ($initialAppPoolState -eq "Started")', [System.StringComparison]::Ordinal)
$stoppedBranchIndex = $wrapperSource.IndexOf('elseif ($initialAppPoolState -eq "Stopped")', [System.StringComparison]::Ordinal)
$unknownBranchIndex = $wrapperSource.IndexOf('else {', $stoppedBranchIndex, [System.StringComparison]::Ordinal)
$backupIndex = $wrapperSource.IndexOf('$databaseFileSet = @(', [System.StringComparison]::Ordinal)
$dotnetIndex = $wrapperSource.IndexOf('& dotnet $trainingAppDll @maintenanceArguments', [System.StringComparison]::Ordinal)

Assert-True ($initialStateQueryIndex -ge 0) "The wrapper does not query the Training app-pool state."
Assert-True ($startedBranchIndex -gt $initialStateQueryIndex) "The Started branch does not follow the initial state query."
Assert-True ($stoppedBranchIndex -gt $startedBranchIndex) "The Stopped branch is missing or out of order."
Assert-True ($unknownBranchIndex -gt $stoppedBranchIndex) "The unknown-state refusal branch is missing or out of order."
Assert-True ($backupIndex -gt $unknownBranchIndex) "Backup logic can run before app-pool state classification."
Assert-True ($dotnetIndex -gt $unknownBranchIndex) "Maintenance can run before app-pool state classification."

$startedBranch = $wrapperSource.Substring($startedBranchIndex, $stoppedBranchIndex - $startedBranchIndex)
$stoppedBranch = $wrapperSource.Substring($stoppedBranchIndex, $unknownBranchIndex - $stoppedBranchIndex)
$unknownBranch = $wrapperSource.Substring($unknownBranchIndex, $backupIndex - $unknownBranchIndex)

Assert-Contains $startedBranch 'Set-AppPoolState -Name $trainingAppPoolName -State "Stopped"' "Started app-pool branch"
Assert-Contains $startedBranch '$restoreAppPoolToStarted = $true' "Started app-pool branch"
Assert-Contains $startedBranch 'Wait-AppPoolState -Name $trainingAppPoolName -DesiredState "Stopped"' "Started app-pool branch"
$restoreGuardIndex = $startedBranch.IndexOf('$restoreAppPoolToStarted = $true', [System.StringComparison]::Ordinal)
$stopRequestIndex = $startedBranch.IndexOf('Set-AppPoolState -Name $trainingAppPoolName -State "Stopped"', [System.StringComparison]::Ordinal)
$waitForStoppedIndex = $startedBranch.IndexOf('Wait-AppPoolState -Name $trainingAppPoolName -DesiredState "Stopped"', [System.StringComparison]::Ordinal)
Assert-True ($restoreGuardIndex -lt $stopRequestIndex) "The restart guard is not set before the stop request."
Assert-True ($waitForStoppedIndex -gt $stopRequestIndex) "The bounded Stopped wait does not follow the stop request."
Assert-True ($backupIndex -gt ($startedBranchIndex + $waitForStoppedIndex)) "Backup logic can run before the bounded Stopped wait."

Assert-Contains $stoppedBranch "is already stopped; preserving that state" "Stopped app-pool branch"
Assert-NotContains $stoppedBranch "Set-AppPoolState" "Stopped app-pool branch"
Assert-NotContains $stoppedBranch '$restoreAppPoolToStarted = $true' "Stopped app-pool branch"

Assert-Contains $unknownBranch "must be Started or Stopped before reset" "Unknown app-pool branch"
Assert-NotContains $unknownBranch "Copy-Item" "Unknown app-pool branch"
Assert-NotContains $unknownBranch "dotnet" "Unknown app-pool branch"

$guardAssignments = [regex]::Matches($wrapperSource, '\$restoreAppPoolToStarted\s*=\s*\$true')
Assert-True ($guardAssignments.Count -eq 1) "The restart guard must be set exactly once when the initial state is Started."
Assert-True ($wrapperSource -match '(?s)finally\s*\{\s*if \(\$restoreAppPoolToStarted\)\s*\{\s*try\s*\{\s*Restore-AppPoolToStarted -Name \$trainingAppPoolName') "Started-state restoration is not protected by finally."

$operationFailureInitializationIndex = $wrapperSource.IndexOf('$operationFailure = $null', [System.StringComparison]::Ordinal)
$restorationFailureInitializationIndex = $wrapperSource.IndexOf('$restorationFailure = $null', [System.StringComparison]::Ordinal)
$operationFailureCaptureIndex = $wrapperSource.IndexOf('$operationFailure = $_', [System.StringComparison]::Ordinal)
$restorationFailureCaptureIndex = $wrapperSource.IndexOf('$restorationFailure = $_', [System.StringComparison]::Ordinal)
$dualFailureBranchIndex = $wrapperSource.IndexOf('if ($null -ne $operationFailure -and $null -ne $restorationFailure) {', [System.StringComparison]::Ordinal)
$operationOnlyBranchIndex = $wrapperSource.IndexOf('if ($null -ne $operationFailure) {', [System.StringComparison]::Ordinal)
$restorationOnlyBranchIndex = $wrapperSource.IndexOf('if ($null -ne $restorationFailure) {', [System.StringComparison]::Ordinal)
$operationFailureLabelIndex = $wrapperSource.IndexOf('Operation failure: $($operationFailure.Exception.Message)', [System.StringComparison]::Ordinal)
$restorationFailureLabelIndex = $wrapperSource.IndexOf('Restoration failure: $($restorationFailure.Exception.Message)', [System.StringComparison]::Ordinal)
$successIndex = $wrapperSource.IndexOf('Write-Step "Success:', [System.StringComparison]::Ordinal)

Assert-True ($operationFailureInitializationIndex -ge 0) "Operation failure state is not initialized."
Assert-True ($restorationFailureInitializationIndex -gt $operationFailureInitializationIndex) "Restoration failure state is not initialized after operation failure state."
Assert-True ($operationFailureCaptureIndex -gt $restorationFailureInitializationIndex) "The operation exception is not captured separately."
Assert-True ($restorationFailureCaptureIndex -gt $operationFailureCaptureIndex) "The restoration exception is not captured separately."
Assert-True ($wrapperSource -match '(?s)catch\s*\{\s*\$operationFailure\s*=\s*\$_\s*\}\s*finally') "Operation failure capture does not precede restoration in finally."
Assert-True ($wrapperSource -match '(?s)Restore-AppPoolToStarted -Name \$trainingAppPoolName\s*\}\s*catch\s*\{\s*\$restorationFailure\s*=\s*\$_') "Restoration failure is not captured from the restoration attempt."
Assert-True ($dualFailureBranchIndex -gt $restorationFailureCaptureIndex) "Dual-failure evaluation does not occur after restoration."
Assert-True ($operationFailureLabelIndex -gt $dualFailureBranchIndex) "The dual-failure message does not contain the labeled operation failure."
Assert-True ($restorationFailureLabelIndex -gt $operationFailureLabelIndex) "Operation failure does not precede Restoration failure in the combined message."
Assert-True ($operationOnlyBranchIndex -gt $restorationFailureLabelIndex) "The operation-only failure branch is missing or out of order."
Assert-True ($restorationOnlyBranchIndex -gt $operationOnlyBranchIndex) "The restoration-only failure branch is missing or out of order."
Assert-True ($successIndex -gt $restorationOnlyBranchIndex) "Success can be reported before all failure checks complete."

$operationOnlyBranch = $wrapperSource.Substring($operationOnlyBranchIndex, $restorationOnlyBranchIndex - $operationOnlyBranchIndex)
$restorationOnlyBranch = $wrapperSource.Substring($restorationOnlyBranchIndex, $successIndex - $restorationOnlyBranchIndex)
Assert-Contains $operationOnlyBranch 'throw $operationFailure' "Operation-only failure branch"
Assert-Contains $restorationOnlyBranch 'throw $restorationFailure' "Restoration-only failure branch"

$waitFunctionStart = $wrapperSource.IndexOf('function Wait-AppPoolState {', [System.StringComparison]::Ordinal)
$restoreFunctionStart = $wrapperSource.IndexOf('function Restore-AppPoolToStarted {', [System.StringComparison]::Ordinal)
$planFunctionStart = $wrapperSource.IndexOf('function Resolve-MaintenancePlan {', [System.StringComparison]::Ordinal)
Assert-True ($waitFunctionStart -ge 0) "The bounded app-pool state wait helper is missing."
Assert-True ($restoreFunctionStart -gt $waitFunctionStart) "The app-pool restoration helper is missing or out of order."
Assert-True ($planFunctionStart -gt $restoreFunctionStart) "The maintenance-plan helper does not follow the app-pool helpers."

$waitFunction = $wrapperSource.Substring($waitFunctionStart, $restoreFunctionStart - $waitFunctionStart)
$restoreFunction = $wrapperSource.Substring($restoreFunctionStart, $planFunctionStart - $restoreFunctionStart)
Assert-Contains $waitFunction '[int]$TimeoutSeconds = 30' "App-pool wait helper"
Assert-Contains $waitFunction '[int]$PollIntervalMilliseconds = 250' "App-pool wait helper"
Assert-Contains $waitFunction '[System.Diagnostics.Stopwatch]::StartNew()' "App-pool wait helper"
Assert-Contains $waitFunction 'Start-Sleep -Milliseconds $sleepMilliseconds' "App-pool wait helper"
Assert-Contains $waitFunction '$stopwatch.Elapsed.TotalMilliseconds -ge $timeoutMilliseconds' "App-pool wait helper"
Assert-Contains $waitFunction 'app pool ''$Name''' "App-pool wait timeout"
Assert-Contains $waitFunction 'reach ''$DesiredState''' "App-pool wait timeout"
Assert-Contains $waitFunction 'Last observed state: ''$lastObservedState''' "App-pool wait timeout"
Assert-NotContains $waitFunction 'while ($true)' "App-pool wait helper"

$restoreStartedIndex = $restoreFunction.IndexOf('"Started" {', [System.StringComparison]::Ordinal)
$restoreStartingIndex = $restoreFunction.IndexOf('"Starting" {', [System.StringComparison]::Ordinal)
$restoreStoppedIndex = $restoreFunction.IndexOf('"Stopped" {', [System.StringComparison]::Ordinal)
$restoreStoppingIndex = $restoreFunction.IndexOf('"Stopping" {', [System.StringComparison]::Ordinal)
$restoreDefaultIndex = $restoreFunction.IndexOf('default {', [System.StringComparison]::Ordinal)
Assert-True ($restoreStartedIndex -ge 0) "Restoration does not account for Started."
Assert-True ($restoreStartingIndex -gt $restoreStartedIndex) "Restoration does not account for Starting."
Assert-True ($restoreStoppedIndex -gt $restoreStartingIndex) "Restoration does not account for Stopped."
Assert-True ($restoreStoppingIndex -gt $restoreStoppedIndex) "Restoration does not account for Stopping."
Assert-True ($restoreDefaultIndex -gt $restoreStoppingIndex) "Restoration does not reject unexpected states."

$restoreStartedBranch = $restoreFunction.Substring($restoreStartedIndex, $restoreStartingIndex - $restoreStartedIndex)
$restoreStartingBranch = $restoreFunction.Substring($restoreStartingIndex, $restoreStoppedIndex - $restoreStartingIndex)
$restoreStoppedBranch = $restoreFunction.Substring($restoreStoppedIndex, $restoreStoppingIndex - $restoreStoppedIndex)
$restoreStoppingBranch = $restoreFunction.Substring($restoreStoppingIndex, $restoreDefaultIndex - $restoreStoppingIndex)
$restoreDefaultBranch = $restoreFunction.Substring($restoreDefaultIndex)
Assert-NotContains $restoreStartedBranch 'Set-AppPoolState' "Started restoration branch"
Assert-Contains $restoreStartingBranch 'Wait-AppPoolState -Name $Name -DesiredState "Started"' "Starting restoration branch"
Assert-NotContains $restoreStartingBranch 'Set-AppPoolState' "Starting restoration branch"

$stoppedStartIndex = $restoreStoppedBranch.IndexOf('Set-AppPoolState -Name $Name -State "Started"', [System.StringComparison]::Ordinal)
$stoppedStartedWaitIndex = $restoreStoppedBranch.IndexOf('Wait-AppPoolState -Name $Name -DesiredState "Started"', [System.StringComparison]::Ordinal)
Assert-True ($stoppedStartIndex -ge 0) "Stopped restoration does not request a start."
Assert-True ($stoppedStartedWaitIndex -gt $stoppedStartIndex) "Stopped restoration does not wait for Started after requesting a start."

$stoppingStoppedWaitIndex = $restoreStoppingBranch.IndexOf('Wait-AppPoolState -Name $Name -DesiredState "Stopped"', [System.StringComparison]::Ordinal)
$stoppingStartIndex = $restoreStoppingBranch.IndexOf('Set-AppPoolState -Name $Name -State "Started"', [System.StringComparison]::Ordinal)
$stoppingStartedWaitIndex = $restoreStoppingBranch.IndexOf('Wait-AppPoolState -Name $Name -DesiredState "Started"', [System.StringComparison]::Ordinal)
Assert-True ($stoppingStoppedWaitIndex -ge 0) "Stopping restoration does not wait for Stopped."
Assert-True ($stoppingStartIndex -gt $stoppingStoppedWaitIndex) "Stopping restoration requests a start before reaching Stopped."
Assert-True ($stoppingStartedWaitIndex -gt $stoppingStartIndex) "Stopping restoration does not wait for Started after requesting a start."
Assert-Contains $restoreDefaultBranch 'unexpected state ''$currentState''' "Unexpected-state restoration branch"
Assert-Contains $wrapperSource 'Get-WebAppPoolState -Name $Name -ErrorAction Stop' "Wrapper source"
Assert-NotContains $wrapperSource "BoardPersistenceOptions__DatabasePath" "Wrapper source"
Assert-NotContains $wrapperSource "--BoardPersistenceOptions" "Wrapper source"

Write-Host "ChairSide Training reset wrapper regression passed."
