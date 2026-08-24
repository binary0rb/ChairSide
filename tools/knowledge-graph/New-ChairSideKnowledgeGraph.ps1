<#
.SYNOPSIS
Generates lightweight ChairSide development knowledge graph indexes.

.DESCRIPTION
Scans the repository for source files and creates mechanical indexes under
docs/knowledge-graph/generated/. This is a private development aid only.
It does not participate in app runtime, build, test, or deployment.
#>

[CmdletBinding()]
param(
    [string]$Root = (Get-Location).Path,
    [string]$OutputDir = "docs/knowledge-graph/generated"
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = "Stop"

function Get-RelativePath {
    param(
        [Parameter(Mandatory = $true)][string]$BasePath,
        [Parameter(Mandatory = $true)][string]$FullPath
    )

    $base = [System.IO.Path]::GetFullPath($BasePath)
    $full = [System.IO.Path]::GetFullPath($FullPath)

    while ($base.EndsWith("\") -or $base.EndsWith("/")) {
        $base = $base.Substring(0, $base.Length - 1)
    }

    if ($full.StartsWith($base, [System.StringComparison]::OrdinalIgnoreCase)) {
        return ($full.Substring($base.Length) -replace '^[\\/]+', '')
    }

    return $FullPath
}

function Convert-ToSlashPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    return ($Path -replace '\\', '/')
}

function Test-ExcludedPath {
    param([Parameter(Mandatory = $true)][string]$RelativePath)

    $normalized = Convert-ToSlashPath -Path $RelativePath
    $wrapped = "/" + $normalized + "/"

    if ($normalized.StartsWith("docs/knowledge-graph/generated/", [System.StringComparison]::OrdinalIgnoreCase)) {
        return $true
    }

    $excludedSegments = @(
        "/.git/",
        "/bin/",
        "/obj/",
        "/node_modules/",
        "/.vs/",
        "/TestResults/",
        "/publish/",
        "/artifacts/",
        "/.claude/",
        "/.playwright-mcp/"
    )

    foreach ($segment in $excludedSegments) {
        if ($wrapped.Contains($segment)) {
            return $true
        }
    }

    return $false
}

function Get-FileKind {
    param([Parameter(Mandatory = $true)][string]$Path)

    $extension = [System.IO.Path]::GetExtension($Path).ToLowerInvariant()

    switch ($extension) {
        ".cs" { return "CSharp" }
        ".cshtml" { return "Razor" }
        ".html" { return "Html" }
        ".css" { return "Css" }
        ".js" { return "JavaScript" }
        ".mjs" { return "JavaScript" }
        ".json" { return "Json" }
        ".yml" { return "Yaml" }
        ".md" { return "Markdown" }
        ".sln" { return "Solution" }
        ".csproj" { return "Project" }
        ".ps1" { return "PowerShell" }
        default { return $extension.TrimStart(".").ToUpperInvariant() }
    }
}

function Select-Matches {
    param(
        [Parameter(Mandatory = $true)][string]$Content,
        [Parameter(Mandatory = $true)][string]$Pattern,
        [AllowNull()]$CodeMask = $null,
        [int]$Max = 40
    )

    $result = @()
    $matches = [System.Text.RegularExpressions.Regex]::Matches(
        $Content,
        $Pattern,
        [System.Text.RegularExpressions.RegexOptions]::Multiline
    )

    foreach ($match in $matches) {
        if ($null -ne $CodeMask) {
            if ($match.Index -ge $CodeMask.Length) {
                continue
            }

            $maskedLength = [Math]::Min($match.Length, $CodeMask.Length - $match.Index)
            $maskedMatch = $CodeMask.Substring($match.Index, $maskedLength)
            if (-not [System.Text.RegularExpressions.Regex]::IsMatch($maskedMatch, '\S')) {
                continue
            }
        }

        if ($match.Groups.Count -gt 1) {
            $value = $match.Groups[1].Value.Trim()

            if ($value -and ($result -notcontains $value)) {
                $result += $value
            }
        }

        if ($result.Count -ge $Max) {
            break
        }
    }

    return $result
}

function Set-CSharpMaskedRange {
    param(
        [Parameter(Mandatory = $true)][System.Text.StringBuilder]$Characters,
        [Parameter(Mandatory = $true)][int]$Start,
        [Parameter(Mandatory = $true)][int]$Length
    )

    $end = [Math]::Min($Characters.Length, $Start + $Length)
    for ($index = $Start; $index -lt $end; $index++) {
        if ($Characters[$index] -ne "`r" -and $Characters[$index] -ne "`n") {
            $Characters[$index] = " "
        }
    }
}

function Get-CSharpCodeMask {
    param([Parameter(Mandatory = $true)][string]$Content)

    $characters = [System.Text.StringBuilder]::new($Content)
    $contentLength = $Content.Length
    $index = 0

    while ($index -lt $contentLength) {
        $literalStart = -1
        $quoteIndex = -1
        $quoteCount = 0
        $isVerbatim = $false
        $isCharacter = $false

        if ($Content[$index] -eq "/" -and $index + 1 -lt $contentLength) {
            if ($Content[$index + 1] -eq "/") {
                $end = $index + 2
                while ($end -lt $contentLength -and $Content[$end] -ne "`r" -and $Content[$end] -ne "`n") {
                    $end++
                }

                Set-CSharpMaskedRange -Characters $characters -Start $index -Length ($end - $index)
                $index = $end
                continue
            }

            if ($Content[$index + 1] -eq "*") {
                $end = $index + 2
                while ($end + 1 -lt $contentLength -and -not ($Content[$end] -eq "*" -and $Content[$end + 1] -eq "/")) {
                    $end++
                }

                if ($end + 1 -lt $contentLength) {
                    $end += 2
                }
                else {
                    $end = $contentLength
                }

                Set-CSharpMaskedRange -Characters $characters -Start $index -Length ($end - $index)
                $index = $end
                continue
            }
        }

        if ($Content[$index] -eq "'") {
            $literalStart = $index
            $quoteIndex = $index
            $quoteCount = 1
            $isCharacter = $true
        }
        elseif ($Content[$index] -eq '"') {
            $literalStart = $index
            $quoteIndex = $index
            while ($quoteIndex + $quoteCount -lt $contentLength -and $Content[$quoteIndex + $quoteCount] -eq '"') {
                $quoteCount++
            }
        }
        elseif ($Content[$index] -eq "@" -and $index + 1 -lt $contentLength -and $Content[$index + 1] -eq '"') {
            $literalStart = $index
            $quoteIndex = $index + 1
            $quoteCount = 1
            $isVerbatim = $true
        }
        elseif ($Content[$index] -eq "@" -and $index + 2 -lt $contentLength -and $Content[$index + 1] -eq '$' -and $Content[$index + 2] -eq '"') {
            $literalStart = $index
            $quoteIndex = $index + 2
            $quoteCount = 1
            $isVerbatim = $true
        }
        elseif ($Content[$index] -eq '$') {
            $prefixEnd = $index
            while ($prefixEnd -lt $contentLength -and $Content[$prefixEnd] -eq '$') {
                $prefixEnd++
            }

            if ($prefixEnd + 1 -lt $contentLength -and $Content[$prefixEnd] -eq "@" -and $Content[$prefixEnd + 1] -eq '"') {
                $literalStart = $index
                $quoteIndex = $prefixEnd + 1
                $quoteCount = 1
                $isVerbatim = $true
            }
            elseif ($prefixEnd -lt $contentLength -and $Content[$prefixEnd] -eq '"') {
                $literalStart = $index
                $quoteIndex = $prefixEnd
                while ($quoteIndex + $quoteCount -lt $contentLength -and $Content[$quoteIndex + $quoteCount] -eq '"') {
                    $quoteCount++
                }
            }
        }

        if ($literalStart -lt 0) {
            $index++
            continue
        }

        if (-not $isCharacter -and $quoteCount -ge 3) {
            $delimiterLength = $quoteCount
            $end = $quoteIndex + $delimiterLength

            while ($end -lt $contentLength) {
                if ($Content[$end] -ne '"') {
                    $end++
                    continue
                }

                $closingQuoteCount = 0
                while ($end + $closingQuoteCount -lt $contentLength -and $Content[$end + $closingQuoteCount] -eq '"') {
                    $closingQuoteCount++
                }

                if ($closingQuoteCount -ge $delimiterLength) {
                    $end += $delimiterLength
                    break
                }

                $end += $closingQuoteCount
            }

            Set-CSharpMaskedRange -Characters $characters -Start $literalStart -Length ($end - $literalStart)
            $index = $end
            continue
        }

        $end = $quoteIndex + 1
        while ($end -lt $contentLength) {
            if ($isVerbatim) {
                if ($Content[$end] -eq '"') {
                    if ($end + 1 -lt $contentLength -and $Content[$end + 1] -eq '"') {
                        $end += 2
                        continue
                    }

                    $end++
                    break
                }

                $end++
                continue
            }

            if ($Content[$end] -eq "\") {
                $end = [Math]::Min($contentLength, $end + 2)
                continue
            }

            $closingCharacter = if ($isCharacter) { "'" } else { '"' }
            if ($Content[$end] -eq $closingCharacter) {
                $end++
                break
            }

            $end++
        }

        Set-CSharpMaskedRange -Characters $characters -Start $literalStart -Length ($end - $literalStart)
        $index = $end
    }

    return $characters.ToString()
}

function Get-DiscoveredSymbols {
    param(
        [Parameter(Mandatory = $true)][string]$Kind,
        [Parameter(Mandatory = $true)][string]$Content
    )

    $symbols = [ordered]@{
        types = @()
        methods = @()
        routes = @()
        hubs = @()
        cssVariables = @()
        scriptFunctions = @()
        headings = @()
    }

    if ($Kind -eq "CSharp" -or $Kind -eq "Razor") {
        $codeMask = Get-CSharpCodeMask -Content $Content
        $symbols.types = @(Select-Matches -Content $Content -CodeMask $codeMask -Pattern '\b(?:public|internal|private|protected)?\s*(?:static\s+)?(?:partial\s+)?(?:class|record|interface|enum|struct)\s+([A-Za-z_][A-Za-z0-9_]*)')
        $symbols.methods = @(Select-Matches -Content $Content -CodeMask $codeMask -Pattern '\b(?:public|internal|private|protected)\s+(?:async\s+)?(?:static\s+)?[A-Za-z0-9_<>,\[\]\?\.]+\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(')
        $symbols.routes = @(Select-Matches -Content $Content -CodeMask $codeMask -Pattern 'Map(?:Get|Post|Put|Delete|Patch|Hub)\s*(?:<[^>]+>)?\s*\(\s*"([^"]+)"')
        $symbols.hubs = @(Select-Matches -Content $Content -CodeMask $codeMask -Pattern 'MapHub\s*<\s*([A-Za-z_][A-Za-z0-9_]*)\s*>')
    }

    if ($Kind -eq "Css") {
        $symbols.cssVariables = @(Select-Matches -Content $Content -Pattern '(--[A-Za-z0-9_-]+)\s*:' -Max 80)
    }

    if ($Kind -eq "JavaScript" -or $Kind -eq "Html" -or $Kind -eq "Razor") {
        $symbols.scriptFunctions = @(Select-Matches -Content $Content -Pattern '\bfunction\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(' -Max 80)
    }

    if ($Kind -eq "Markdown") {
        $symbols.headings = @(Select-Matches -Content $Content -Pattern '^#{1,4}\s+(.+)$' -Max 80)
    }

    return $symbols
}

function New-Node {
    param(
        [Parameter(Mandatory = $true)][string]$Id,
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][string]$Type,
        [string]$Path = ""
    )

    return [pscustomobject][ordered]@{
        id = $Id
        label = $Label
        type = $Type
        path = $Path
    }
}

function New-Edge {
    param(
        [Parameter(Mandatory = $true)][string]$From,
        [Parameter(Mandatory = $true)][string]$To,
        [Parameter(Mandatory = $true)][string]$Label
    )

    return [pscustomobject][ordered]@{
        from = $From
        to = $To
        label = $Label
    }
}

$rootPath = [System.IO.Path]::GetFullPath($Root)
$outputPath = Join-Path $rootPath $OutputDir

New-Item -ItemType Directory -Force -Path $outputPath | Out-Null

$allowedExtensions = @(
    ".cs",
    ".cshtml",
    ".html",
    ".css",
    ".js",
    ".mjs",
    ".json",
    ".yml",
    ".md",
    ".sln",
    ".csproj",
    ".ps1"
)

$files = Get-ChildItem -Path $rootPath -Recurse -File |
    Where-Object {
        $relative = Get-RelativePath -BasePath $rootPath -FullPath $_.FullName
        ($allowedExtensions -contains $_.Extension.ToLowerInvariant()) -and -not (Test-ExcludedPath -RelativePath $relative)
    } |
    Sort-Object FullName

$index = @()
$nodes = @()
$edges = @()

$nodes += New-Node -Id "repo:chairside" -Label "ChairSide repository" -Type "Repository"

foreach ($file in $files) {
    $relativePath = Get-RelativePath -BasePath $rootPath -FullPath $file.FullName
    $slashPath = Convert-ToSlashPath -Path $relativePath
    $kind = Get-FileKind -Path $file.FullName

    $content = Get-Content -Path $file.FullName -Raw -ErrorAction SilentlyContinue
    if ($null -eq $content) {
        $content = ""
    }

    $symbols = Get-DiscoveredSymbols -Kind $kind -Content $content
    $fileNodeId = "file:" + $slashPath

    $nodes += New-Node -Id $fileNodeId -Label $slashPath -Type "File" -Path $slashPath
    $edges += New-Edge -From "repo:chairside" -To $fileNodeId -Label "contains"

    foreach ($typeName in $symbols.types) {
        $typeNodeId = "symbol:type:" + $typeName
        $nodes += New-Node -Id $typeNodeId -Label $typeName -Type "CodeType" -Path $slashPath
        $edges += New-Edge -From $fileNodeId -To $typeNodeId -Label "declares"
    }

    foreach ($route in $symbols.routes) {
        $routeNodeId = "route:" + $route
        $nodes += New-Node -Id $routeNodeId -Label $route -Type "Route" -Path $slashPath
        $edges += New-Edge -From $fileNodeId -To $routeNodeId -Label "maps"
    }

    foreach ($hub in $symbols.hubs) {
        $hubNodeId = "hub:" + $hub
        $nodes += New-Node -Id $hubNodeId -Label $hub -Type "SignalRHub" -Path $slashPath
        $edges += New-Edge -From $fileNodeId -To $hubNodeId -Label "maps-hub"
    }

    $index += [pscustomobject][ordered]@{
        path = $slashPath
        kind = $kind
        sizeBytes = $file.Length
        typeSymbols = @($symbols.types)
        methodSymbols = @($symbols.methods)
        routes = @($symbols.routes)
        hubs = @($symbols.hubs)
        cssVariables = @($symbols.cssVariables)
        scriptFunctions = @($symbols.scriptFunctions)
        headings = @($symbols.headings)
    }
}

$dedupedNodes = @($nodes | Group-Object -Property id | ForEach-Object { $_.Group[0] } | Sort-Object -Property id)
$dedupedEdges = @($edges | Sort-Object -Property from, to, label -Unique)

$generatedStamp = "deterministic"

$symbolIndex = [ordered]@{
    generatedStamp = $generatedStamp
    root = "."
    fileCount = $index.Count
    files = @($index)
}

$graphData = [ordered]@{
    generatedStamp = $generatedStamp
    description = "Mechanical ChairSide repo graph generated from source files. Use docs/knowledge-graph/chairside.graph.md for human-authored architecture intent."
    nodes = @($dedupedNodes)
    edges = @($dedupedEdges)
}

$symbolJsonPath = Join-Path $outputPath "symbol-index.json"
$graphJsonPath = Join-Path $outputPath "graph-data.json"
$fileInventoryPath = Join-Path $outputPath "file-inventory.md"

$symbolIndex | ConvertTo-Json -Depth 8 | Set-Content -Path $symbolJsonPath -Encoding UTF8
$graphData | ConvertTo-Json -Depth 8 | Set-Content -Path $graphJsonPath -Encoding UTF8

$inventoryLines = @()
$inventoryLines += "# ChairSide generated file inventory"
$inventoryLines += ""
$inventoryLines += "Generated output is deterministic. No timestamp is written."
$inventoryLines += ""
$inventoryLines += "This file is mechanical output from ``tools/knowledge-graph/New-ChairSideKnowledgeGraph.ps1``. Review diffs before committing."
$inventoryLines += ""
$inventoryLines += "| Path | Kind | Discovered symbols | Routes / hubs |"
$inventoryLines += "| --- | --- | --- | --- |"

foreach ($item in ($index | Sort-Object -Property path)) {
    $symbolParts = @()

    if ($item.typeSymbols.Count -gt 0) {
        $symbolParts += "types: " + (($item.typeSymbols | Select-Object -First 8) -join ", ")
    }

    if ($item.methodSymbols.Count -gt 0) {
        $symbolParts += "methods: " + (($item.methodSymbols | Select-Object -First 8) -join ", ")
    }

    if ($item.cssVariables.Count -gt 0) {
        $symbolParts += "css vars: " + (($item.cssVariables | Select-Object -First 8) -join ", ")
    }

    if ($item.scriptFunctions.Count -gt 0) {
        $symbolParts += "functions: " + (($item.scriptFunctions | Select-Object -First 8) -join ", ")
    }

    if ($item.headings.Count -gt 0) {
        $symbolParts += "headings: " + (($item.headings | Select-Object -First 8) -join ", ")
    }

    $routeParts = @()

    if ($item.routes.Count -gt 0) {
        $routeParts += "routes: " + (($item.routes | Select-Object -First 8) -join ", ")
    }

    if ($item.hubs.Count -gt 0) {
        $routeParts += "hubs: " + (($item.hubs | Select-Object -First 8) -join ", ")
    }

    if ($symbolParts.Count -gt 0) {
        $symbolsText = $symbolParts -join "<br>"
    }
    else {
        $symbolsText = "-"
    }

    if ($routeParts.Count -gt 0) {
        $routesText = $routeParts -join "<br>"
    }
    else {
        $routesText = "-"
    }

    $safePath = $item.path -replace '\|', '\|'
    $inventoryLines += "| ``$safePath`` | $($item.kind) | $symbolsText | $routesText |"
}

$inventoryLines | Set-Content -Path $fileInventoryPath -Encoding UTF8

Write-Host "ChairSide knowledge graph artifacts generated:"
Write-Host "  $fileInventoryPath"
Write-Host "  $symbolJsonPath"
Write-Host "  $graphJsonPath"


