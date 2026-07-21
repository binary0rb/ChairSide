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

function Assert-NotContains {
    param([string]$Actual, [string]$Unexpected, [string]$Label)
    if ($Actual.IndexOf($Unexpected, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "$Label unexpectedly contained '$Unexpected'. Actual output:`n$Actual"
    }
}

function Invoke-Wrapper {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $wrapperPath @Arguments 2>&1 | Out-String
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    return [pscustomobject]@{
        ExitCode = $exitCode
        Output = $output
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
Assert-Contains $wrongToken.Output "Confirmation token does not match" "Wrong-token refusal"
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
    Assert-Contains $result.Output "-CompletedCycles is valid only with -Mode ReportingVolume" "$mode completed-cycle refusal"
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
Assert-Contains $startedBranch '$stoppedAppPoolState = Get-AppPoolState -Name $trainingAppPoolName' "Started app-pool branch"
Assert-Contains $startedBranch 'if ($stoppedAppPoolState -ne "Stopped")' "Started app-pool branch"
Assert-Contains $startedBranch '$appPoolTransitionedFromStarted = $true' "Started app-pool branch"
Assert-True ($startedBranch.IndexOf('$appPoolTransitionedFromStarted = $true', [System.StringComparison]::Ordinal) -gt $startedBranch.IndexOf('if ($stoppedAppPoolState -ne "Stopped")', [System.StringComparison]::Ordinal)) "The restart guard is set before the Stopped state is confirmed."

Assert-Contains $stoppedBranch "is already stopped; preserving that state" "Stopped app-pool branch"
Assert-NotContains $stoppedBranch "Set-AppPoolState" "Stopped app-pool branch"
Assert-NotContains $stoppedBranch '$appPoolTransitionedFromStarted = $true' "Stopped app-pool branch"

Assert-Contains $unknownBranch "must be Started or Stopped before reset" "Unknown app-pool branch"
Assert-NotContains $unknownBranch "Copy-Item" "Unknown app-pool branch"
Assert-NotContains $unknownBranch "dotnet" "Unknown app-pool branch"

$guardAssignments = [regex]::Matches($wrapperSource, '\$appPoolTransitionedFromStarted\s*=\s*\$true')
Assert-True ($guardAssignments.Count -eq 1) "The restart guard must be set exactly once, only after a confirmed Started-to-Stopped transition."
Assert-True ($wrapperSource -match '(?s)finally\s*\{\s*if \(\$appPoolTransitionedFromStarted\)\s*\{\s*Set-AppPoolState -Name \$trainingAppPoolName -State "Started"') "The confirmed Started-to-Stopped transition is not protected by the expected finally-based restart."
Assert-Contains $wrapperSource 'Get-WebAppPoolState -Name $Name -ErrorAction Stop' "Wrapper source"
Assert-NotContains $wrapperSource "BoardPersistenceOptions__DatabasePath" "Wrapper source"
Assert-NotContains $wrapperSource "--BoardPersistenceOptions" "Wrapper source"

Write-Host "ChairSide Training reset wrapper regression passed."
