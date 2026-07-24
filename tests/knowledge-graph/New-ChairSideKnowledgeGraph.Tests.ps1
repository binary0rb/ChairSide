[CmdletBinding()]
param()

Set-StrictMode -Version 2.0
$ErrorActionPreference = "Stop"

function Assert-SequenceEqual {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Expected,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Actual,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $difference = @(Compare-Object -ReferenceObject $Expected -DifferenceObject $Actual -SyncWindow 0)
    if ($difference.Count -gt 0) {
        throw "$Label differed. Expected '$($Expected -join ', ')'; actual '$($Actual -join ', ')'."
    }
}

function Get-ArtifactHashes {
    param([Parameter(Mandatory = $true)][string]$OutputPath)

    $hashes = [ordered]@{}
    foreach ($name in @("file-inventory.md", "graph-data.json", "symbol-index.json")) {
        $hashes[$name] = (Get-FileHash -Algorithm SHA256 -Path (Join-Path $OutputPath $name)).Hash
    }

    return $hashes
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$generatorPath = Join-Path $repositoryRoot "tools/knowledge-graph/New-ChairSideKnowledgeGraph.ps1"
$fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("chairside-knowledge-graph-" + [Guid]::NewGuid().ToString("N"))
$fixturePath = Join-Path $fixtureRoot "ExtractorFixture.cs"
$moduleFixturePath = Join-Path $fixtureRoot "eslint.config.mjs"
$workflowFixturePath = Join-Path $fixtureRoot "WorkflowFixture.yml"
$outputPath = Join-Path $fixtureRoot "docs/knowledge-graph/generated"

$fixture = @'
namespace ExtractorFixture;

/// record the finished cycle without declaring a type.
/// incomplete-assignment record and the active room are saved together.
/// the abort record carries no terminal handoff link.
// Inserts one aborted-assignment record on the caller's transaction.
// public void CommentMethod() { }
// app.MapGet("/comment-route", () => true);
/* interface BlockCommentType
   app.MapHub<BlockCommentHub>("/block-comment-hub"); */
public sealed class LegitimateType
{
    private const string Regular = "class RegularStringType; public void RegularStringMethod() { }";
    private const string Verbatim = @"record VerbatimStringType; public void VerbatimStringMethod() { }";
    private const string Interpolated = $"interface InterpolatedStringType {1}";
    private const string InterpolatedVerbatim = $@"enum InterpolatedVerbatimType {1}";
    private const string Raw = """
        struct RawStringType
        public void RawStringMethod() { }
        app.MapGet("/raw-string-route", () => true);
        app.MapHub<RawStringHub>("/raw-string-hub");
        """;
    private const string InterpolatedRaw = $$"""class InterpolatedRawType {{1}}""";
    private const char CharacterLiteral = '\'';

    public void LegitimateMethod() { }
}

app.MapGet("/api/legitimate", () => "record ResponseStringType");
app.MapHub<LegitimateHub>("/legitimateHub");
'@

$moduleFixture = "export function lintConfigFactory() { return []; }"
$workflowFixture = "name: Fixture workflow`non:`n  push:"

try {
    New-Item -ItemType Directory -Force -Path $fixtureRoot | Out-Null
    [System.IO.File]::WriteAllText($fixturePath, $fixture, [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::WriteAllText($moduleFixturePath, $moduleFixture, [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::WriteAllText($workflowFixturePath, $workflowFixture, [System.Text.UTF8Encoding]::new($false))

    & $generatorPath -Root $fixtureRoot | Out-Null

    $symbolIndexPath = Join-Path $outputPath "symbol-index.json"
    $graphDataPath = Join-Path $outputPath "graph-data.json"
    $symbolIndex = Get-Content -Raw $symbolIndexPath | ConvertFrom-Json
    $graphData = Get-Content -Raw $graphDataPath | ConvertFrom-Json
    $entry = @($symbolIndex.files | Where-Object { $_.path -eq "ExtractorFixture.cs" })

    if ($entry.Count -ne 1) {
        throw "Expected exactly one fixture entry, found $($entry.Count)."
    }

    $moduleEntry = @(
        $symbolIndex.files |
            Where-Object { $_.path -eq "eslint.config.mjs" }
    )

    $workflowEntry = @(
        $symbolIndex.files |
            Where-Object { $_.path -eq "WorkflowFixture.yml" }
    )

    if ($moduleEntry.Count -ne 1) {
        throw "Expected exactly one MJS fixture entry, found $($moduleEntry.Count)."
    }

    if ($workflowEntry.Count -ne 1) {
        throw "Expected exactly one YML fixture entry, found $($workflowEntry.Count)."
    }

    if ($moduleEntry[0].kind -ne "JavaScript") {
        throw "MJS fixture was not classified as JavaScript."
    }

    if ($workflowEntry[0].kind -ne "Yaml") {
        throw "YML fixture was not classified as Yaml."
    }

    $inventory = Get-Content -Raw (Join-Path $outputPath "file-inventory.md")

    foreach ($fixtureEntry in @("eslint.config.mjs", "WorkflowFixture.yml")) {
        if (-not $inventory.Contains($fixtureEntry)) {
            throw "Generated inventory omitted fixture: $fixtureEntry"
        }
    }

    Assert-SequenceEqual `
        -Label "MJS functions" `
        -Expected @("lintConfigFactory") `
        -Actual @($moduleEntry[0].scriptFunctions)

    Assert-SequenceEqual -Label "Types" -Expected @("LegitimateType") -Actual @($entry[0].typeSymbols)
    Assert-SequenceEqual -Label "Methods" -Expected @("LegitimateMethod") -Actual @($entry[0].methodSymbols)
    Assert-SequenceEqual -Label "Routes" -Expected @("/api/legitimate", "/legitimateHub") -Actual @($entry[0].routes)
    Assert-SequenceEqual -Label "Hubs" -Expected @("LegitimateHub") -Actual @($entry[0].hubs)

    foreach ($falsePositive in @("the", "and", "carries", "on", "RegularStringType", "RawStringType")) {
        if (@($entry[0].typeSymbols) -contains $falsePositive) {
            throw "Comment or string false positive '$falsePositive' was extracted as a type."
        }
    }

    foreach ($falseNodeId in @("symbol:type:the", "symbol:type:RawStringType", "route:/raw-string-route", "hub:RawStringHub")) {
        if (@($graphData.nodes.id) -contains $falseNodeId) {
            throw "Comment or string false-positive graph node '$falseNodeId' was generated."
        }
    }

    $firstHashes = Get-ArtifactHashes -OutputPath $outputPath
    & $generatorPath -Root $fixtureRoot | Out-Null
    $secondHashes = Get-ArtifactHashes -OutputPath $outputPath

    foreach ($name in $firstHashes.Keys) {
        if ($firstHashes[$name] -ne $secondHashes[$name]) {
            throw "$name was not byte-identical across consecutive fixture runs."
        }
    }

    Get-Content -Raw $symbolIndexPath | ConvertFrom-Json | Out-Null
    Get-Content -Raw $graphDataPath | ConvertFrom-Json | Out-Null
    Write-Host "Knowledge graph extraction and file coverage regression passed."
}
finally {
    $resolvedTemp = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    $resolvedFixture = [System.IO.Path]::GetFullPath($fixtureRoot)
    if ($resolvedFixture.StartsWith($resolvedTemp, [System.StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolvedFixture -Recurse -Force -ErrorAction SilentlyContinue
    }
}
