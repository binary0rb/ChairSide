<#
.SYNOPSIS
    Stops the ChairSide Training app pool, backs up its SQLite file set, and resets the Training
    sandbox to one of the approved operator fixtures.

.DESCRIPTION
    This wrapper is deliberately limited to the code-owned ChairSide Training deployment. Paths,
    app-pool identity, and child environment cannot be overridden. The application maintenance CLI
    still validates the Training database identity before any reset or seed mutation.

.EXAMPLE
    .\Reset-ChairSideTrainingData.ps1 -Mode Clean -ConfirmationToken RESET_EMPTY_BETA -WhatIf

.EXAMPLE
    .\Reset-ChairSideTrainingData.ps1 -Mode FullStress -ConfirmationToken RESET_STRESS_FIXTURE
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = "Medium")]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("Clean", "TrainingSeed", "FullStress", "ReportingVolume")]
    [string]$Mode,

    [Parameter(Mandatory = $true)]
    [string]$ConfirmationToken,

    [ValidateRange(100, 10000)]
    [int]$CompletedCycles
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = "Stop"

$trainingAppRoot = "C:\ChairSide\Training\App"
$trainingAppDll = "C:\ChairSide\Training\App\ChairSide.Board.dll"
$trainingDataRoot = "C:\ChairSide\Training\Data"
$trainingDatabasePath = "C:\ChairSide\Training\Data\chairside-training.db"
$trainingBackupRoot = "C:\ChairSide\Training\Backups"
$trainingAppPoolName = "ChairSideBoard-Training"
$trainingEnvironment = "Training"
$trainingConfigurationPath = "C:\ChairSide\Training\App\appsettings.Training.json"
$trainingLogDirectory = "C:\ChairSide\Training\Logs"

function Write-Step {
    param([string]$Message)
    Write-Host "[ChairSide Training Reset] $Message"
}

function Assert-ExactPath {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Actual,
        [Parameter(Mandatory = $true)][string]$Expected
    )

    $normalizedActual = [System.IO.Path]::GetFullPath($Actual).TrimEnd('\')
    $normalizedExpected = [System.IO.Path]::GetFullPath($Expected).TrimEnd('\')
    if (-not [string]::Equals($normalizedActual, $normalizedExpected, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Name must be '$Expected'. Received '$Actual'."
    }
}

function Copy-IfPresent {
    param([string]$SourcePath, [string]$DestinationPath)
    if (Test-Path -LiteralPath $SourcePath) {
        Copy-Item -LiteralPath $SourcePath -Destination $DestinationPath -Force -ErrorAction Stop
        Write-Step "Backed up $(Split-Path -Leaf $SourcePath)."
        return $true
    }

    return $false
}

function Set-AppPoolState {
    param([string]$Name, [string]$State)

    Import-Module WebAdministration -ErrorAction Stop
    if ($State -eq "Stopped") {
        Write-Step "Stopping IIS app pool '$Name'."
        Stop-WebAppPool -Name $Name
        return
    }

    if ($State -eq "Started") {
        Write-Step "Starting IIS app pool '$Name'."
        Start-WebAppPool -Name $Name
        return
    }

    throw "Unsupported requested app-pool state '$State'."
}

function Get-AppPoolState {
    param([string]$Name)

    Import-Module WebAdministration -ErrorAction Stop
    $stateResult = Get-WebAppPoolState -Name $Name -ErrorAction Stop
    $state = [string]$stateResult.Value
    if ([string]::IsNullOrWhiteSpace($state)) {
        throw "IIS app pool '$Name' did not return a state."
    }

    return $state
}

function Wait-AppPoolState {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$DesiredState,
        [int]$TimeoutSeconds = 30,
        [int]$PollIntervalMilliseconds = 250
    )

    $timeoutMilliseconds = $TimeoutSeconds * 1000
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $lastObservedState = Get-AppPoolState -Name $Name

    while ($lastObservedState -ne $DesiredState) {
        if ($stopwatch.Elapsed.TotalMilliseconds -ge $timeoutMilliseconds) {
            throw "Timed out after $TimeoutSeconds seconds waiting for IIS app pool '$Name' to reach '$DesiredState'. Last observed state: '$lastObservedState'."
        }

        $remainingMilliseconds = [Math]::Max(1, $timeoutMilliseconds - $stopwatch.Elapsed.TotalMilliseconds)
        $sleepMilliseconds = [Math]::Min($PollIntervalMilliseconds, [int][Math]::Ceiling($remainingMilliseconds))
        Start-Sleep -Milliseconds $sleepMilliseconds
        $lastObservedState = Get-AppPoolState -Name $Name
    }

    return $lastObservedState
}

function Restore-AppPoolToStarted {
    param([Parameter(Mandatory = $true)][string]$Name)

    $currentState = Get-AppPoolState -Name $Name
    switch ($currentState) {
        "Started" {
            Write-Step "IIS app pool '$Name' is already started; restoration is complete."
            return
        }
        "Starting" {
            $null = Wait-AppPoolState -Name $Name -DesiredState "Started"
            return
        }
        "Stopped" {
            Set-AppPoolState -Name $Name -State "Started"
            $null = Wait-AppPoolState -Name $Name -DesiredState "Started"
            return
        }
        "Stopping" {
            $null = Wait-AppPoolState -Name $Name -DesiredState "Stopped"
            Set-AppPoolState -Name $Name -State "Started"
            $null = Wait-AppPoolState -Name $Name -DesiredState "Started"
            return
        }
        default {
            throw "Cannot restore IIS app pool '$Name' to Started from unexpected state '$currentState'."
        }
    }
}

function Resolve-MaintenancePlan {
    param(
        [string]$SelectedMode,
        [bool]$IncludeCompletedCycles,
        [int]$RequestedCompletedCycles
    )

    switch ($SelectedMode) {
        "Clean" {
            return [pscustomobject]@{
                Command = "reset-empty"
                RequiredToken = "RESET_EMPTY_BETA"
                ExtraArguments = @()
            }
        }
        "TrainingSeed" {
            return [pscustomobject]@{
                Command = "reset-training-data"
                RequiredToken = "RESET_TRAINING_DATA"
                ExtraArguments = @()
            }
        }
        "FullStress" {
            return [pscustomobject]@{
                Command = "reset-stress-fixture"
                RequiredToken = "RESET_STRESS_FIXTURE"
                ExtraArguments = @("--profile", "full-stress")
            }
        }
        "ReportingVolume" {
            $arguments = @("--profile", "reporting-volume")
            if ($IncludeCompletedCycles) {
                $arguments += @("--completed-cycles", $RequestedCompletedCycles.ToString([System.Globalization.CultureInfo]::InvariantCulture))
            }

            return [pscustomobject]@{
                Command = "reset-stress-fixture"
                RequiredToken = "RESET_STRESS_FIXTURE"
                ExtraArguments = $arguments
            }
        }
    }

    throw "Unsupported Training reset mode '$SelectedMode'."
}

function Write-Plan {
    param(
        [Parameter(Mandatory = $true)]$Plan,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    Write-Step "Mode:              $Mode"
    Write-Step "App root:          $trainingAppRoot"
    Write-Step "App DLL:           $trainingAppDll"
    Write-Step "Configuration:     $trainingConfigurationPath"
    Write-Step "Data root:         $trainingDataRoot"
    Write-Step "Database:          $trainingDatabasePath"
    Write-Step "Backup root:       $trainingBackupRoot"
    Write-Step "App pool:          $trainingAppPoolName"
    Write-Step "Child environment: $trainingEnvironment"
    Write-Step "CLI command:       dotnet $trainingAppDll $($Arguments -join ' ')"
    Write-Step "Required token:    $($Plan.RequiredToken)"
}

$completedCyclesSupplied = $PSBoundParameters.ContainsKey("CompletedCycles")
if ($completedCyclesSupplied -and $Mode -ne "ReportingVolume") {
    Write-Error "[ChairSide Training Reset] -CompletedCycles is valid only with -Mode ReportingVolume. No action was taken."
    exit 1
}

$plan = Resolve-MaintenancePlan `
    -SelectedMode $Mode `
    -IncludeCompletedCycles $completedCyclesSupplied `
    -RequestedCompletedCycles $CompletedCycles
if (-not [string]::Equals($ConfirmationToken, $plan.RequiredToken, [System.StringComparison]::Ordinal)) {
    Write-Error "[ChairSide Training Reset] Confirmation token does not match. Mode '$Mode' requires -ConfirmationToken $($plan.RequiredToken). No action was taken."
    exit 1
}

$maintenanceArguments = @("--maintenance", $plan.Command, "--confirm", $plan.RequiredToken) + @($plan.ExtraArguments)

try {
    Assert-ExactPath -Name "Training app root" -Actual $trainingAppRoot -Expected "C:\ChairSide\Training\App"
    Assert-ExactPath -Name "Training app DLL" -Actual $trainingAppDll -Expected "C:\ChairSide\Training\App\ChairSide.Board.dll"
    Assert-ExactPath -Name "Training data root" -Actual $trainingDataRoot -Expected "C:\ChairSide\Training\Data"
    Assert-ExactPath -Name "Training database" -Actual $trainingDatabasePath -Expected "C:\ChairSide\Training\Data\chairside-training.db"
    Assert-ExactPath -Name "Training backup root" -Actual $trainingBackupRoot -Expected "C:\ChairSide\Training\Backups"
    if ($trainingAppPoolName -ne "ChairSideBoard-Training" -or $trainingEnvironment -ne "Training") {
        throw "Training app-pool or environment constants are invalid."
    }

    Write-Plan -Plan $plan -Arguments $maintenanceArguments

    $operation = "preserve pool state, back up SQLite files, run '$($plan.Command)', and restore pool state"
    if ($WhatIfPreference) {
        $null = $PSCmdlet.ShouldProcess($trainingDatabasePath, $operation)
        Write-Step "WhatIf completed. No IIS, filesystem, or application action was performed."
        exit 0
    }

    if (-not (Test-Path -LiteralPath $trainingAppDll -PathType Leaf)) {
        throw "ChairSide.Board.dll not found at '$trainingAppDll'."
    }

    if (-not (Test-Path -LiteralPath $trainingConfigurationPath -PathType Leaf)) {
        throw "Training configuration not found at '$trainingConfigurationPath'."
    }

    $trainingConfiguration = Get-Content -Raw -LiteralPath $trainingConfigurationPath | ConvertFrom-Json
    $configuredDatabasePath = $trainingConfiguration.BoardPersistenceOptions.DatabasePath
    $configuredLogDirectory = $trainingConfiguration.DiagnosticOptions.LogDirectory
    Assert-ExactPath -Name "Configured Training database" -Actual $configuredDatabasePath -Expected $trainingDatabasePath
    Assert-ExactPath -Name "Configured Training log directory" -Actual $configuredLogDirectory -Expected $trainingLogDirectory

    if (-not $PSCmdlet.ShouldProcess($trainingDatabasePath, $operation)) {
        Write-Step "Reset canceled. No action was performed."
        exit 0
    }

    $restoreAppPoolToStarted = $false
    $backupDirectory = $null
    $operationFailure = $null
    $restorationFailure = $null
    try {
        $initialAppPoolState = Get-AppPoolState -Name $trainingAppPoolName
        if ($initialAppPoolState -eq "Started") {
            $restoreAppPoolToStarted = $true
            Set-AppPoolState -Name $trainingAppPoolName -State "Stopped"
            $null = Wait-AppPoolState -Name $trainingAppPoolName -DesiredState "Stopped"
        }
        elseif ($initialAppPoolState -eq "Stopped") {
            Write-Step "IIS app pool '$trainingAppPoolName' is already stopped; preserving that state."
        }
        else {
            throw "IIS app pool '$trainingAppPoolName' must be Started or Stopped before reset. Current state: '$initialAppPoolState'."
        }

        $databaseFileSet = @(
            $trainingDatabasePath,
            "$trainingDatabasePath-wal",
            "$trainingDatabasePath-shm"
        )
        $existingDatabaseFiles = @($databaseFileSet | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
        if ($existingDatabaseFiles.Count -gt 0) {
            if (-not (Test-Path -LiteralPath $trainingBackupRoot -PathType Container)) {
                New-Item -ItemType Directory -Path $trainingBackupRoot -Force | Out-Null
            }

            $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
            $backupDirectory = Join-Path $trainingBackupRoot "chairside-training-pre-$Mode-$timestamp"
            New-Item -ItemType Directory -Path $backupDirectory -ErrorAction Stop | Out-Null

            foreach ($databaseFile in $databaseFileSet) {
                $destination = Join-Path $backupDirectory ([System.IO.Path]::GetFileName($databaseFile))
                $null = Copy-IfPresent -SourcePath $databaseFile -DestinationPath $destination
            }
            Write-Step "Backup created: $backupDirectory"
        }
        else {
            Write-Step "No Training SQLite files exist; no backup was required."
        }

        $previousEnvironment = $env:ASPNETCORE_ENVIRONMENT
        try {
            $env:ASPNETCORE_ENVIRONMENT = $trainingEnvironment
            Push-Location $trainingAppRoot
            try {
                Write-Step "Running Training maintenance CLI."
                & dotnet $trainingAppDll @maintenanceArguments
                if ($LASTEXITCODE -ne 0) {
                    throw "Training maintenance CLI exited with code $LASTEXITCODE."
                }
            }
            finally {
                Pop-Location
            }
        }
        finally {
            $env:ASPNETCORE_ENVIRONMENT = $previousEnvironment
        }
    }
    catch {
        $operationFailure = $_
    }
    finally {
        if ($restoreAppPoolToStarted) {
            try {
                Restore-AppPoolToStarted -Name $trainingAppPoolName
            }
            catch {
                $restorationFailure = $_
            }
        }
    }

    if ($null -ne $operationFailure -and $null -ne $restorationFailure) {
        throw "Operation failure: $($operationFailure.Exception.Message)`nRestoration failure: $($restorationFailure.Exception.Message)"
    }

    if ($null -ne $operationFailure) {
        throw $operationFailure
    }

    if ($null -ne $restorationFailure) {
        throw $restorationFailure
    }

    $backupSummary = if ($null -eq $backupDirectory) { "none required" } else { $backupDirectory }
    Write-Step "Success: mode $Mode completed; backup: $backupSummary; app pool: $trainingAppPoolName."
    exit 0
}
catch {
    Write-Error "[ChairSide Training Reset] Failed: $($_.Exception.Message)"
    exit 1
}
