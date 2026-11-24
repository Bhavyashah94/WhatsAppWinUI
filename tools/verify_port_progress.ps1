<#
.SYNOPSIS
    Automated verification script for WhatsAppWinUI port tracking inventory.
.DESCRIPTION
    Cross-checks docs/port_progress.md against reference/whatsapp-rust to ensure:
    1. 100% of crates (11) are present.
    2. 100% of production source files (581) are present without omissions and zero test file pollution.
    3. 100% of production functions are present without omissions and zero test function leaks.
    4. Valid status enums ('Complete', 'In Progress', 'Not Started').
    5. Physical existence on disk in WhatsAppWinUI for files marked Complete or In Progress,
       and genuine existence of target C# symbols for Complete status.
    6. Executive Dashboard and Crate Roll-Up metrics match actual enumerated table row totals.
    
    Exits with code 0 on 100% verification, code 1 on any omission or mismatch.
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = "",
    [string]$ReferenceRoot = "",
    [string]$ProgressDoc = "",
    [switch]$CheckDisk = $true,
    [switch]$Detailed = $false
)

$ErrorActionPreference = 'Stop'

# Auto-resolve paths if not supplied
if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    $RepoRoot = if ($scriptDir -and (Test-Path (Join-Path $scriptDir "..\reference\whatsapp-rust"))) {
        (Resolve-Path (Join-Path $scriptDir "..")).Path
    } elseif (Test-Path "reference\whatsapp-rust") {
        (Get-Location).Path
    } else {
        throw "Could not resolve RepoRoot automatically; pass -RepoRoot explicitly."
    }
}
if ([string]::IsNullOrWhiteSpace($ReferenceRoot)) {
    $ReferenceRoot = Join-Path $RepoRoot "reference\whatsapp-rust"
}
if ([string]::IsNullOrWhiteSpace($ProgressDoc)) {
    $ProgressDoc = Join-Path $RepoRoot "docs\port_progress.md"
}

Write-Host "======================================================================" -ForegroundColor Cyan
Write-Host " WhatsAppWinUI Port Progress & Inventory Verification Script" -ForegroundColor Cyan
Write-Host "======================================================================" -ForegroundColor Cyan
Write-Host "Repository Root:   $RepoRoot"
Write-Host "Reference Root:    $ReferenceRoot"
Write-Host "Tracking Doc:      $ProgressDoc"
Write-Host "Check Disk Assets: $CheckDisk"
Write-Host "----------------------------------------------------------------------"

# 11 Core Crates
$crates = @(
    'src',
    'wacore',
    'wacore/appstate',
    'wacore/binary',
    'wacore/derive',
    'wacore/libsignal',
    'wacore/noise',
    'waproto',
    'storages/sqlite-storage',
    'transports/tokio-transport',
    'http_clients/ureq-client'
)

# Test file filter
function Is-TestFile($fullName, $crate) {
    if ($fullName -match '\\tests\\' -or $fullName -match '\\benches\\' -or $fullName -match '\\target\\' -or $fullName -match '\\fuzz\\') {
        return $true
    }
    if ($crate -eq 'wacore') {
        if ($fullName -match '\\appstate\\' -or $fullName -match '\\binary\\' -or $fullName -match '\\derive\\' -or $fullName -match '\\libsignal\\' -or $fullName -match '\\noise\\') {
            return $true
        }
    }
    $fileName = [System.IO.Path]::GetFileName($fullName)
    if ($fileName -match 'tests?\.rs$' -or $fileName -match '_tests?\.rs$' -or $fileName -match '^test_' -or 
        $fullName -match '\\test_support\\' -or $fullName -match '\\test_utils\\' -or $fullName -match '\\test_util\.rs$') {
        return $true
    }
    return $false
}

# Robust line-by-line Rust parser excluding test functions and modules
function Parse-RustProductionFunctions($filePath) {
    $lines = [System.IO.File]::ReadAllLines($filePath)
    $inBlockComment = $false
    
    $inTestMod = $false
    $testModBraceLevel = 0
    
    $inTestFn = $false
    $testFnBraceLevel = 0
    
    $braceLevel = 0
    $isTestAttrPending = $false
    
    $functions = [System.Collections.Generic.List[PSCustomObject]]::new()
    
    for ($i = 0; $i -lt $lines.Length; $i++) {
        $line = $lines[$i]
        $trimmed = $line.Trim()
        
        # Block comments
        if ($inBlockComment) {
            $endIdx = $trimmed.IndexOf('*/')
            if ($endIdx -ge 0) {
                $inBlockComment = $false
                $line = $trimmed.Substring($endIdx + 2)
                $trimmed = $line.Trim()
            } else {
                continue
            }
        }
        
        if ($trimmed.StartsWith('/*')) {
            $endIdx = $trimmed.IndexOf('*/')
            if ($endIdx -ge 0) {
                $trimmed = $trimmed.Substring($endIdx + 2).Trim()
            } else {
                $inBlockComment = $true
                continue
            }
        }
        
        # Line comments
        if ($trimmed.StartsWith('//')) {
            continue
        }
        
        $commentIdx = $line.IndexOf('//')
        $cleanLine = if ($commentIdx -ge 0) { $line.Substring(0, $commentIdx) } else { $line }
        
        # Attribute tracking
        if ($trimmed.StartsWith('#[')) {
            if ($cleanLine -match '#\[.*test.*\]' -or 
                $cleanLine -match '#\[cfg\(.*(?:test|bench).*' -or 
                $cleanLine -match '#\[cfg\(any\(.*test.*' -or 
                $cleanLine -match '#\[cfg\(all\(.*test.*') {
                $isTestAttrPending = $true
            }
        }
        
        # mod tests / testing tracking
        if ($cleanLine -match '\bmod\s+([a-zA-Z0-9_]+)') {
            $modName = $matches[1]
            if ($isTestAttrPending -or $modName -match 'tests?|testing') {
                $inTestMod = $true
                $testModBraceLevel = $braceLevel
            }
            $isTestAttrPending = $false
        }
        
        $openCount = ([regex]::Matches($cleanLine, '\{')).Count
        $closeCount = ([regex]::Matches($cleanLine, '\}')).Count
        
        # Match function declaration
        if ($cleanLine -match '^\s*(pub(?:\s*\([^)]+\))?\s+)?(?:(?:const|async|unsafe|extern(?:\s+"[^"]+")?)\s+)*fn\s+([a-zA-Z0-9_]+)\s*(?:<[^>]*>)?\s*\(') {
            $rawVis = $matches[1]
            $name = $matches[2]
            
            $visType = 'private'
            $displayVis = 'private'
            if ($rawVis) {
                $v = $rawVis.Trim()
                $displayVis = $v
                if ($v -eq 'pub') {
                    $visType = 'pub'
                } else {
                    $visType = 'internal'
                }
            }
            
            $isTest = $inTestMod -or $inTestFn -or $isTestAttrPending -or 
                      ($name -like 'test_*') -or ($name -like '*_for_test') -or ($name -eq 'for_test') -or ($name -like '*_test')
            
            if ($isTest) {
                if (-not $inTestFn -and -not $inTestMod) {
                    $inTestFn = $true
                    $testFnBraceLevel = $braceLevel
                }
            } else {
                $functions.Add([PSCustomObject]@{
                    Line = $i + 1
                    Name = $name
                    Visibility = $visType
                    DisplayVis = $displayVis
                    RawVis = $rawVis
                })
            }
            
            $isTestAttrPending = $false
        } elseif (-not $trimmed.StartsWith('#') -and -not [string]::IsNullOrWhiteSpace($trimmed)) {
            $isTestAttrPending = $false
        }
        
        $braceLevel += ($openCount - $closeCount)
        
        if ($inTestFn -and $braceLevel -le $testFnBraceLevel) {
            $inTestFn = $false
        }
        if ($inTestMod -and $braceLevel -le $testModBraceLevel) {
            $inTestMod = $false
        }
    }
    
    return $functions
}

# Phase 1: Enumerate Upstream
Write-Host "Scanning upstream Rust repository at: $ReferenceRoot..." -ForegroundColor Yellow
$sw = [System.Diagnostics.Stopwatch]::StartNew()

$upstreamFiles = [System.Collections.Generic.Dictionary[string, PSCustomObject]]::new([System.StringComparer]::OrdinalIgnoreCase)
$upstreamUniqueFunctions = [System.Collections.Generic.Dictionary[string, PSCustomObject]]::new([System.StringComparer]::OrdinalIgnoreCase)
$upstreamAllFunctions = [System.Collections.Generic.List[PSCustomObject]]::new()
$upstreamCratesFound = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

foreach ($c in $crates) {
    $p = if ($c -eq 'src') { Join-Path $ReferenceRoot 'src' } else { Join-Path $ReferenceRoot ($c -replace '/', '\') }
    if (-not (Test-Path $p)) {
        Write-Warning "Crate directory not found: $p"
        continue
    }
    
    [void]$upstreamCratesFound.Add($c)
    
    $files = Get-ChildItem -Path $p -Filter "*.rs" -Recurse | Where-Object { 
        -not (Is-TestFile $_.FullName $c)
    }
    
    foreach ($f in $files) {
        $relPath = $f.FullName.Substring($ReferenceRoot.Length).TrimStart('\', '/') -replace '\\', '/'
        $fns = Parse-RustProductionFunctions $f.FullName
        
        $upstreamFiles[$relPath] = [PSCustomObject]@{
            Crate = $c
            RelativePath = $relPath
            Functions = $fns
        }
        
        foreach ($fn in $fns) {
            $fnObj = [PSCustomObject]@{
                Crate = $c
                File = $relPath
                Name = $fn.Name
                Visibility = $fn.Visibility
            }
            $upstreamAllFunctions.Add($fnObj)
            
            $fnKey = "$relPath::$($fn.Name)"
            $upstreamUniqueFunctions[$fnKey] = $fnObj
        }
    }
}
$sw.Stop()

Write-Host ("Upstream Scan Complete: {0} crates, {1} files, {2} functions ({3} unique symbols) in {4} ms" -f $upstreamCratesFound.Count, $upstreamFiles.Count, $upstreamAllFunctions.Count, $upstreamUniqueFunctions.Count, $sw.ElapsedMilliseconds) -ForegroundColor Green
Write-Host "----------------------------------------------------------------------"

# Phase 2: Parse docs/port_progress.md
if (-not (Test-Path $ProgressDoc)) {
    Write-Host "[FAIL] Progress document does not exist: $ProgressDoc" -ForegroundColor Red
    exit 1
}

Write-Host "Parsing Markdown tracking document: $ProgressDoc..." -ForegroundColor Yellow
$sw.Restart()

$docLines = [System.IO.File]::ReadAllLines($ProgressDoc)
$docCrates = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
$docFiles = [System.Collections.Generic.Dictionary[string, PSCustomObject]]::new([System.StringComparer]::OrdinalIgnoreCase)
$docUniqueFunctions = [System.Collections.Generic.Dictionary[string, PSCustomObject]]::new([System.StringComparer]::OrdinalIgnoreCase)
$docAllFunctions = [System.Collections.Generic.List[PSCustomObject]]::new()

$overallMetrics = @{}
$crateRollUpRows = @{}
$currentCrateForTable = ""

$inDetailedTable = $false
$inSummaryTable = $false
$inOverallMetricsTable = $false
$inCrateRollUpTable = $false

$validStatuses = @('Complete', 'In Progress', 'Not Started')
$errors = [System.Collections.Generic.List[string]]::new()

for ($lineIdx = 0; $lineIdx -lt $docLines.Length; $lineIdx++) {
    $line = $docLines[$lineIdx].Trim()
    
    if ($line.StartsWith('## Crate:')) {
        if ($line -match '##\s+Crate:\s*([a-zA-Z0-9_\-\/]+)') {
            $currentCrateForTable = $matches[1].Trim()
            [void]$docCrates.Add($currentCrateForTable)
        }
    }
    
    if ($line -eq '### Overall Metrics') {
        $inOverallMetricsTable = $true
        $inCrateRollUpTable = $false
        $inDetailedTable = $false
        $inSummaryTable = $false
        continue
    }
    if ($line -eq '### Crate Roll-Up Summary') {
        $inCrateRollUpTable = $true
        $inOverallMetricsTable = $false
        $inDetailedTable = $false
        $inSummaryTable = $false
        continue
    }
    
    if ($line.StartsWith('|') -and $line.EndsWith('|')) {
        if ($line -match '^\|\s*[-:]+\s*\|') {
            continue
        }
        
        $trimmedContent = "$line".Trim('|')
        $cells = [regex]::Split($trimmedContent, '(?<!\\)\|') | ForEach-Object { "$_".Trim() }
        
        # Identify headers
        if ($cells[0] -eq 'Metric') {
            continue
        }
        if ($cells[0] -eq 'Upstream Crate' -and $cells[1] -match 'Package') {
            continue
        }
        if ($cells[0] -eq 'Upstream File') {
            if ($cells.Length -ge 7 -and $cells[1] -match 'Symbol') {
                $inDetailedTable = $true
                $inSummaryTable = $false
                $inOverallMetricsTable = $false
                $inCrateRollUpTable = $false
                continue
            } elseif ($cells.Length -ge 5 -and $cells[1] -match 'Functions') {
                $inSummaryTable = $true
                $inDetailedTable = $false
                $inOverallMetricsTable = $false
                $inCrateRollUpTable = $false
                continue
            }
        }
        
        # Overall Metrics Table Row: 6 columns
        if ($inOverallMetricsTable -and $cells.Length -ge 6) {
            $metricName = $cells[0].Trim('`', ' ')
            $overallMetrics[$metricName] = [PSCustomObject]@{
                Line = $lineIdx + 1
                Metric = $metricName
                Total = [int]($cells[1] -replace '\D', '')
                Complete = [int]($cells[2] -replace '\D', '')
                InProgress = [int]($cells[3] -replace '\D', '')
                NotStarted = [int]($cells[4] -replace '\D', '')
                Pct = $cells[5]
            }
            continue
        }
        
        # Crate Roll-Up Table Row: 10 columns
        if ($inCrateRollUpTable -and $cells.Length -ge 10) {
            $crateRaw = $cells[0].Trim('`', '*', ' ')
            $crateRollUpRows[$crateRaw] = [PSCustomObject]@{
                Line = $lineIdx + 1
                Crate = $crateRaw
                Package = $cells[1].Trim('`', '*', ' ')
                Files = [int]($cells[2] -replace '\D', '')
                PubInt = [int]($cells[3] -replace '\D', '')
                Priv = [int]($cells[4] -replace '\D', '')
                Total = [int]($cells[5] -replace '\D', '')
                Complete = [int]($cells[6] -replace '\D', '')
                InProgress = [int]($cells[7] -replace '\D', '')
                NotStarted = [int]($cells[8] -replace '\D', '')
                Pct = $cells[9]
            }
            continue
        }
        
        # Detailed Function Row: 7 columns
        if ($inDetailedTable -and $cells.Length -ge 7) {
            $fileRaw = $cells[0].Trim('`', ' ') -replace '\\', '/'
            $symRaw = $cells[1].Trim('`', ' ')
            $symName = if ($symRaw -match '::([a-zA-Z0-9_]+)') { $matches[1] } elseif ($symRaw -match '^([a-zA-Z0-9_]+)') { $matches[1] } else { $symRaw }
            
            $vis = $cells[2].Trim('`', ' ')
            $csFile = $cells[3].Trim('`', ' ')
            $csSym = $cells[4].Trim('`', ' ')
            $status = $cells[5].Trim('`', ' ')
            $notes = $cells[6]
            
            if ($status -notin $validStatuses) {
                $errors.Add("Line $($lineIdx + 1): Invalid Status '$status' for $fileRaw::$symName. Must be one of: $($validStatuses -join ', ')")
            }
            
            $fnObj = [PSCustomObject]@{
                Line = $lineIdx + 1
                Crate = $currentCrateForTable
                File = $fileRaw
                Symbol = $symName
                Visibility = $vis
                TargetCsFile = $csFile
                TargetCsSymbol = $csSym
                Status = $status
                Notes = $notes
            }
            
            $docAllFunctions.Add($fnObj)
            $docFnKey = "$fileRaw::$symName"
            $docUniqueFunctions[$docFnKey] = $fnObj
        }
        
        # File Summary Row: 5 columns
        if ($inSummaryTable -and $cells.Length -ge 5) {
            $fileRaw = $cells[0].Trim('`', ' ') -replace '\\', '/'
            $pubInt = [int]($cells[1] -replace '\D', '')
            $totalFns = [int]($cells[2] -replace '\D', '')
            $csFile = $cells[3].Trim('`', ' ')
            $fileStatus = $cells[4].Trim('`', ' ')
            
            if ($fileStatus -notin $validStatuses) {
                $errors.Add("Line $($lineIdx + 1): Invalid File Status '$fileStatus' for $fileRaw. Must be one of: $($validStatuses -join ', ')")
            }
            
            $docFiles[$fileRaw] = [PSCustomObject]@{
                Line = $lineIdx + 1
                Crate = $currentCrateForTable
                File = $fileRaw
                PubIntCount = $pubInt
                TotalCount = $totalFns
                TargetCsFile = $csFile
                Status = $fileStatus
            }
        }
    } else {
        if ([string]::IsNullOrWhiteSpace($line)) {
            $inDetailedTable = $false
            $inSummaryTable = $false
            $inOverallMetricsTable = $false
            $inCrateRollUpTable = $false
        }
    }
}
$sw.Stop()

Write-Host ("Markdown Parsed: {0} crates, {1} file summaries, {2} function rows ({3} unique symbols) in {4} ms" -f $docCrates.Count, $docFiles.Count, $docAllFunctions.Count, $docUniqueFunctions.Count, $sw.ElapsedMilliseconds) -ForegroundColor Green
Write-Host "----------------------------------------------------------------------"

# Phase 3: Cross-Checking Invariants
Write-Host "Executing Validation Invariants..." -ForegroundColor Yellow

# Invariant 1: Crate Coverage
$missingCrates = [System.Collections.Generic.List[string]]::new()
foreach ($c in $upstreamCratesFound) {
    if (-not $docCrates.Contains($c)) {
        $missingCrates.Add($c)
        $errors.Add("Invariant 1 Violation: Missing Crate in tracking doc: '$c'")
    }
}

# Invariant 2: File Coverage
$missingFiles = [System.Collections.Generic.List[string]]::new()
$extraFiles = [System.Collections.Generic.List[string]]::new()

foreach ($f in $upstreamFiles.Keys) {
    if (-not $docFiles.ContainsKey($f)) {
        $missingFiles.Add($f)
        $errors.Add("Invariant 2 Violation: Missing File in tracking doc: '$f'")
    }
}
foreach ($f in $docFiles.Keys) {
    if (-not $upstreamFiles.ContainsKey($f)) {
        $extraFiles.Add($f)
        $errors.Add("Invariant 2 Violation: Extraneous or Test File in tracking doc: '$f'")
    }
}

# Invariant 3: Function Coverage
$missingFunctions = [System.Collections.Generic.List[string]]::new()
$extraFunctions = [System.Collections.Generic.List[string]]::new()

foreach ($fnKey in $upstreamUniqueFunctions.Keys) {
    if (-not $docUniqueFunctions.ContainsKey($fnKey)) {
        $missingFunctions.Add($fnKey)
        $errors.Add("Invariant 3 Violation: Missing Function in tracking doc: '$fnKey'")
    }
}
foreach ($fnKey in $docUniqueFunctions.Keys) {
    if (-not $upstreamUniqueFunctions.ContainsKey($fnKey)) {
        $extraFunctions.Add($fnKey)
        $errors.Add("Invariant 3 Violation: Extraneous or Test Function in tracking doc: '$fnKey'")
    }
}

if ($docAllFunctions.Count -ne $upstreamAllFunctions.Count) {
    $errors.Add("Invariant 3 Violation: Total enumerated functions in tracking doc ($($docAllFunctions.Count)) does not match upstream total ($($upstreamAllFunctions.Count))")
}

# Invariant 4: Physical C# Disk Verification & Symbol Matching
$diskMissingFiles = [System.Collections.Generic.List[string]]::new()
$symbolMissing = [System.Collections.Generic.List[string]]::new()
$cachedCsContent = @{}

if ($CheckDisk) {
    foreach ($entry in $docAllFunctions) {
        if ($entry.Status -in @('Complete', 'In Progress')) {
            $csPath = $entry.TargetCsFile
            if ($csPath -and $csPath -notin @('TBD', 'N/A', '-', 'None')) {
                $fullCsPath = Join-Path $RepoRoot ($csPath -replace '/', '\')
                if (-not (Test-Path $fullCsPath)) {
                    $diskMissingFiles.Add("$($entry.File)::$($entry.Symbol) -> $csPath")
                    $errors.Add("Invariant 4 Violation: Referenced C# file not found on disk: '$csPath' (for $($entry.File)::$($entry.Symbol))")
                } else {
                    # Verify C# symbol existence if Complete
                    if ($entry.Status -eq 'Complete') {
                        $sym = $entry.TargetCsSymbol
                        if ($sym -in @('-', 'TBD', 'N/A', 'None', '')) {
                            $symbolMissing.Add("$($entry.File)::$($entry.Symbol) -> empty symbol")
                            $errors.Add("Invariant 4 Violation: Symbol marked Complete has invalid TargetCsSymbol '$sym' (for $($entry.File)::$($entry.Symbol))")
                        } else {
                            if (-not $cachedCsContent.ContainsKey($fullCsPath)) {
                                $cachedCsContent[$fullCsPath] = [System.IO.File]::ReadAllText($fullCsPath)
                            }
                            $cSharpText = $cachedCsContent[$fullCsPath]
                            
                            # Extract member name (token after last '.')
                            $memberName = if ($sym -match '\.([a-zA-Z0-9_]+)$') { $matches[1] } else { $sym }
                            
                            # Check whether member name exists in C# file
                            if ($cSharpText -notmatch "\b$memberName\b") {
                                $symbolMissing.Add("$($entry.File)::$($entry.Symbol) -> $sym not in $csPath")
                                $errors.Add("Invariant 4 Violation: Target C# Symbol '$memberName' does not exist in $csPath (for $($entry.File)::$($entry.Symbol))")
                            }
                        }
                    }
                }
            } else {
                if ($entry.Status -eq 'Complete') {
                    $errors.Add("Invariant 4 Violation: Function marked Complete lacks a Target C# File: $($entry.File)::$($entry.Symbol)")
                }
            }
        } elseif ($entry.Status -eq 'Not Started') {
            if ($entry.TargetCsFile -ne '-' -and $entry.TargetCsFile -ne 'TBD') {
                $errors.Add("Invariant 4 Violation: Function marked Not Started should have '-' for Target C# File: $($entry.File)::$($entry.Symbol)")
            }
            if ($entry.TargetCsSymbol -ne '-' -and $entry.TargetCsSymbol -ne 'TBD') {
                $errors.Add("Invariant 4 Violation: Function marked Not Started should have '-' for Target C# Symbol: $($entry.File)::$($entry.Symbol)")
            }
        }
    }
}

# Invariant 5: Executive Dashboard & Crate Roll-Up Consistency
$dashboardErrors = [System.Collections.Generic.List[string]]::new()

# Compute actual counts from parsed doc entries
$actualTotalFiles = $docFiles.Count
$actualTotalFns = $docAllFunctions.Count
$actualCompleteFns = @($docAllFunctions | Where-Object { $_.Status -eq 'Complete' }).Count
$actualInProgressFns = @($docAllFunctions | Where-Object { $_.Status -eq 'In Progress' }).Count
$actualNotStartedFns = @($docAllFunctions | Where-Object { $_.Status -eq 'Not Started' }).Count

$actualPubFns = @($docAllFunctions | Where-Object { $_.Visibility -eq 'pub' }).Count
$actualIntFns = @($docAllFunctions | Where-Object { $_.Visibility -like 'pub(*' }).Count
$actualPrivFns = @($docAllFunctions | Where-Object { $_.Visibility -eq 'private' -or $_.Visibility -eq 'fn' }).Count

$actualCompletePub = @($docAllFunctions | Where-Object { $_.Visibility -eq 'pub' -and $_.Status -eq 'Complete' }).Count
$actualCompleteInt = @($docAllFunctions | Where-Object { $_.Visibility -like 'pub(*' -and $_.Status -eq 'Complete' }).Count
$actualCompletePriv = @($docAllFunctions | Where-Object { ($_.Visibility -eq 'private' -or $_.Visibility -eq 'fn') -and $_.Status -eq 'Complete' }).Count

$actualCompleteFiles = @($docFiles.Values | Where-Object { $_.Status -eq 'Complete' }).Count
$actualInProgressFiles = @($docFiles.Values | Where-Object { $_.Status -eq 'In Progress' }).Count
$actualNotStartedFiles = @($docFiles.Values | Where-Object { $_.Status -eq 'Not Started' }).Count

# Check Overall Metrics
foreach ($mKey in $overallMetrics.Keys) {
    $row = $overallMetrics[$mKey]
    $rowSum = $row.Complete + $row.InProgress + $row.NotStarted
    if ($rowSum -ne $row.Total) {
        $dashboardErrors.Add("Overall Metric '$mKey': Complete ($($row.Complete)) + InProgress ($($row.InProgress)) + NotStarted ($($row.NotStarted)) = $rowSum != Total ($($row.Total))")
    }
    
    if ($mKey -match 'Total Upstream Crates') {
        if ($row.Total -ne $docCrates.Count) {
            $dashboardErrors.Add("Overall Metric '$mKey' Total ($($row.Total)) != Crates Count ($($docCrates.Count))")
        }
    } elseif ($mKey -match 'Total Production Files') {
        if ($row.Total -ne $actualTotalFiles) {
            $dashboardErrors.Add("Overall Metric '$mKey' Total ($($row.Total)) != File Count ($actualTotalFiles)")
        }
        if ($row.Complete -ne $actualCompleteFiles) {
            $dashboardErrors.Add("Overall Metric '$mKey' Complete ($($row.Complete)) != Complete File Count ($actualCompleteFiles)")
        }
    } elseif ($mKey -match 'Total Production Functions') {
        if ($row.Total -ne $actualTotalFns) {
            $dashboardErrors.Add("Overall Metric '$mKey' Total ($($row.Total)) != Function Count ($actualTotalFns)")
        }
        if ($row.Complete -ne $actualCompleteFns) {
            $dashboardErrors.Add("Overall Metric '$mKey' Complete ($($row.Complete)) != Complete Function Count ($actualCompleteFns)")
        }
    } elseif ($mKey -match 'Public APIs') {
        if ($row.Total -ne $actualPubFns) {
            $dashboardErrors.Add("Overall Metric '$mKey' Total ($($row.Total)) != Pub Count ($actualPubFns)")
        }
        if ($row.Complete -ne $actualCompletePub) {
            $dashboardErrors.Add("Overall Metric '$mKey' Complete ($($row.Complete)) != Complete Pub Count ($actualCompletePub)")
        }
    } elseif ($mKey -match 'Internal APIs') {
        if ($row.Total -ne $actualIntFns) {
            $dashboardErrors.Add("Overall Metric '$mKey' Total ($($row.Total)) != Internal Count ($actualIntFns)")
        }
        if ($row.Complete -ne $actualCompleteInt) {
            $dashboardErrors.Add("Overall Metric '$mKey' Complete ($($row.Complete)) != Complete Internal Count ($actualCompleteInt)")
        }
    } elseif ($mKey -match 'Private Functions') {
        if ($row.Total -ne $actualPrivFns) {
            $dashboardErrors.Add("Overall Metric '$mKey' Total ($($row.Total)) != Priv Count ($actualPrivFns)")
        }
        if ($row.Complete -ne $actualCompletePriv) {
            $dashboardErrors.Add("Overall Metric '$mKey' Complete ($($row.Complete)) != Complete Priv Count ($actualCompletePriv)")
        }
    }
}

# Check Crate Roll-Up Summary
foreach ($crateName in $crateRollUpRows.Keys) {
    if ($crateName -eq 'Total') {
        $totRow = $crateRollUpRows['Total']
        if ($totRow.Files -ne $actualTotalFiles) {
            $dashboardErrors.Add("Roll-Up Total Files ($($totRow.Files)) != Actual Files ($actualTotalFiles)")
        }
        if ($totRow.Total -ne $actualTotalFns) {
            $dashboardErrors.Add("Roll-Up Total Functions ($($totRow.Total)) != Actual Functions ($actualTotalFns)")
        }
        if ($totRow.Complete -ne $actualCompleteFns) {
            $dashboardErrors.Add("Roll-Up Total Complete ($($totRow.Complete)) != Actual Complete Functions ($actualCompleteFns)")
        }
        continue
    }
    
    $cRow = $crateRollUpRows[$crateName]
    $cSum = $cRow.Complete + $cRow.InProgress + $cRow.NotStarted
    if ($cSum -ne $cRow.Total) {
        $dashboardErrors.Add("Crate Roll-Up '$crateName': Complete ($($cRow.Complete)) + InProgress ($($cRow.InProgress)) + NotStarted ($($cRow.NotStarted)) = $cSum != Total ($($cRow.Total))")
    }
    
    # Cross check with per-crate files and functions
    $crateFiles = @($docFiles.Values | Where-Object { $_.Crate -eq $crateName }).Count
    if ($cRow.Files -ne $crateFiles) {
        $dashboardErrors.Add("Crate Roll-Up '$crateName' Files ($($cRow.Files)) != Actual Files ($crateFiles)")
    }
    $crateFns = @($docAllFunctions | Where-Object { $_.Crate -eq $crateName }).Count
    if ($cRow.Total -ne $crateFns) {
        $dashboardErrors.Add("Crate Roll-Up '$crateName' Functions ($($cRow.Total)) != Actual Functions ($crateFns)")
    }
    $crateComp = @($docAllFunctions | Where-Object { $_.Crate -eq $crateName -and $_.Status -eq 'Complete' }).Count
    if ($cRow.Complete -ne $crateComp) {
        $dashboardErrors.Add("Crate Roll-Up '$crateName' Complete ($($cRow.Complete)) != Actual Complete Functions ($crateComp)")
    }
}

foreach ($de in $dashboardErrors) {
    $errors.Add("Invariant 5 Violation: $de")
}

# Phase 4: Output & Exit
Write-Host "======================================================================" -ForegroundColor Cyan
Write-Host " Verification Summary Report" -ForegroundColor Cyan
Write-Host "======================================================================" -ForegroundColor Cyan
Write-Host ("Crates:    Upstream = {0,4} | Documented = {1,4} | Missing = {2}" -f $upstreamCratesFound.Count, $docCrates.Count, $missingCrates.Count)
Write-Host ("Files:     Upstream = {0,4} | Documented = {1,4} | Missing = {2} | Extra = {3}" -f $upstreamFiles.Count, $docFiles.Count, $missingFiles.Count, $extraFiles.Count)
Write-Host ("Functions: Upstream = {0,4} | Documented = {1,4} | Missing = {2} | Extra = {3}" -f $upstreamAllFunctions.Count, $docAllFunctions.Count, $missingFunctions.Count, $extraFunctions.Count)
if ($CheckDisk) {
    Write-Host ("Disk Check:Physical C# Files Verified for Complete/In Progress | Missing = {0}" -f $diskMissingFiles.Count)
    Write-Host ("Symbol Check: Target C# Symbols Verified for Complete Status | Missing = {0}" -f $symbolMissing.Count)
}
Write-Host ("Dashboard Check: Metric Consistency & Arithmetic Checks | Errors = {0}" -f $dashboardErrors.Count)
Write-Host "----------------------------------------------------------------------"

if ($errors.Count -eq 0) {
    Write-Host "[PASS] 100% of upstream inventory accounted for with ZERO omissions and ZERO test leaks!" -ForegroundColor Green
    exit 0
} else {
    Write-Host ("[FAIL] Verification encountered {0} error(s):" -f $errors.Count) -ForegroundColor Red
    $displayLimit = [Math]::Min(25, $errors.Count)
    for ($e = 0; $e -lt $displayLimit; $e++) {
        Write-Host "  - $($errors[$e])" -ForegroundColor Red
    }
    if ($errors.Count -gt $displayLimit) {
        Write-Host "  ... and $($errors.Count - $displayLimit) additional error(s)." -ForegroundColor Yellow
    }
    exit 1
}
