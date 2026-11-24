<#
.SYNOPSIS
    Automated generator for docs/port_progress.md
.DESCRIPTION
    Scans the 11 production crates in reference/whatsapp-rust, parses production source files
    and genuine production functions (excluding all unit tests and test suites), maps verified
    C# symbols against WhatsAppWinUI on disk, and writes the single source of truth markdown inventory.
#>
param(
    [string]$RepoRoot = "",
    [string]$ReferenceRoot = "",
    [string]$OutputFile = ""
)

$ErrorActionPreference = 'Stop'

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
if ([string]::IsNullOrWhiteSpace($OutputFile)) {
    $OutputFile = Join-Path $RepoRoot "docs\port_progress.md"
}

$bt = [char]96

Write-Host "RepoRoot:      $RepoRoot"
Write-Host "ReferenceRoot: $ReferenceRoot"
Write-Host "OutputFile:    $OutputFile"

# 11 Core Production Crates
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

$cratePackages = @{
    'src' = 'whatsapp-rust'
    'wacore' = 'wacore'
    'wacore/appstate' = 'wacore-appstate'
    'wacore/binary' = 'wacore-binary'
    'wacore/derive' = 'wacore-derive'
    'wacore/libsignal' = 'wacore-libsignal'
    'wacore/noise' = 'wacore-noise'
    'waproto' = 'waproto'
    'storages/sqlite-storage' = 'whatsapp-rust-sqlite-storage'
    'transports/tokio-transport' = 'whatsapp-rust-tokio-transport'
    'http_clients/ureq-client' = 'whatsapp-rust-ureq-http-client'
}

$crateProjects = @{
    'src' = 'src/WhatsApp.Client/WhatsApp.Client.csproj'
    'wacore' = 'wacore/binary/WhatsApp.WaCore.Binary.csproj'
    'wacore/appstate' = 'wacore/appstate/WhatsApp.WaCore.AppState.csproj'
    'wacore/binary' = 'wacore/binary/WhatsApp.WaCore.Binary.csproj'
    'wacore/derive' = 'wacore/binary/WhatsApp.WaCore.Binary.csproj'
    'wacore/libsignal' = 'wacore/libsignal/WhatsApp.WaCore.LibSignal.csproj'
    'wacore/noise' = 'wacore/noise/WhatsApp.WaCore.Noise.csproj'
    'waproto' = 'waproto/WhatsApp.WaProto.csproj'
    'storages/sqlite-storage' = 'storages/sqlite-storage/WhatsApp.Storage.Sqlite.csproj'
    'transports/tokio-transport' = 'transports/socket-transport/WhatsApp.Transport.Sockets.csproj'
    'http_clients/ureq-client' = 'http_clients/http-client/WhatsApp.Http.csproj'
}

$crateProjectPaths = @{
    'src' = 'src/WhatsApp.Client'
    'wacore' = 'wacore/binary'
    'wacore/appstate' = 'wacore/appstate'
    'wacore/binary' = 'wacore/binary'
    'wacore/derive' = 'wacore/binary'
    'wacore/libsignal' = 'wacore/libsignal'
    'wacore/noise' = 'wacore/noise'
    'waproto' = 'waproto'
    'storages/sqlite-storage' = 'storages/sqlite-storage'
    'transports/tokio-transport' = 'transports/socket-transport'
    'http_clients/ureq-client' = 'http_clients/http-client'
}

$crateStatuses = @{
    'src' = 'In Progress'
    'wacore' = 'In Progress'
    'wacore/appstate' = 'In Progress'
    'wacore/binary' = 'Complete'
    'wacore/derive' = 'In Progress'
    'wacore/libsignal' = 'In Progress'
    'wacore/noise' = 'Complete'
    'waproto' = 'Complete'
    'storages/sqlite-storage' = 'Complete'
    'transports/tokio-transport' = 'Complete'
    'http_clients/ureq-client' = 'Complete'
}

$crateNotes = @{
    'src' = 'Core protocol coordinator, QR auth, pairing'
    'wacore' = 'Core protocol abstractions and shared primitives'
    'wacore/appstate' = 'App state synchronization actions & patch decoding'
    'wacore/binary' = 'Binary XML tokenizer, JID parser, token dictionary'
    'wacore/derive' = 'ProtocolNode attributes and serialization models'
    'wacore/libsignal' = 'Double Ratchet session cipher, PreKey bundles'
    'wacore/noise' = 'Curve25519, XEdDSA, Noise XX/IK handshakes'
    'waproto' = 'Protobuf models, AdvProto, HistorySync, WaCodec'
    'storages/sqlite-storage' = 'SQLite credentials, identity keys, session ratchets'
    'transports/tokio-transport' = "WebSocket duplex transport (${bt}WhatsAppSocket${bt})"
    'http_clients/ureq-client' = "HistorySync chunk downloader (${bt}HistorySyncDownloader${bt})"
}

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

# Index C# files and methods
$csFiles = Get-ChildItem -Path $RepoRoot -Filter "*.cs" -Recurse | Where-Object {
    $_.FullName -notmatch '\\obj\\' -and $_.FullName -notmatch '\\bin\\' -and $_.FullName -notmatch '\\reference\\' -and $_.FullName -notmatch '\\tests\\' -and $_.FullName -notmatch '\\WhatsApp\.Desktop\\'
}

$csIndex = @{}
$csClassMap = @{}

foreach ($cs in $csFiles) {
    $rel = $cs.FullName.Substring($RepoRoot.Length).TrimStart('\') -replace '\\', '/'
    $lines = [System.IO.File]::ReadAllLines($cs.FullName)
    $members = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $primaryClass = [System.IO.Path]::GetFileNameWithoutExtension($cs.FullName)
    $ns = "WhatsApp"
    
    foreach ($l in $lines) {
        if ($l -match 'namespace\s+([a-zA-Z0-9_\.]+)') {
            $ns = $matches[1]
        }
        if ($l -match '^\s*(?:public|protected|internal|private)?\s*(?:static\s+)?(?:async\s+)?(?:virtual\s+)?(?:override\s+)?(?:[\w<>\[\],\?]+)\s+([A-Za-z0-9_]+)\s*(?:<[^>]*>)?\s*\(') {
            $name = $matches[1]
            if ($name -notin @('if', 'for', 'foreach', 'while', 'switch', 'catch', 'lock', 'using', 'sizeof', 'typeof', 'nameof', 'return')) {
                [void]$members.Add($name)
            }
        }
        if ($l -match '^\s*(?:public|protected|internal|private)?\s*(?:static\s+)?(?:virtual\s+)?(?:override\s+)?(?:[\w<>\[\],\?]+)\s+([A-Za-z0-9_]+)\s*\{\s*(?:get|set|init)') {
            [void]$members.Add($matches[1])
        }
        if ($l -match '(?:class|struct|record|interface)\s+([a-zA-Z0-9_]+)') {
            [void]$members.Add($matches[1])
        }
    }
    $csIndex[$rel] = $members
    $csClassMap[$rel] = "$ns.$primaryClass"
}

function To-PascalCase($str) {
    $parts = $str.Split('_', [System.StringSplitOptions]::RemoveEmptyEntries)
    $sb = [System.Text.StringBuilder]::new()
    foreach ($p in $parts) {
        if ($p.Length -gt 0) {
            $sb.Append([char]::ToUpperInvariant($p[0])) | Out-Null
            if ($p.Length -gt 1) {
                $sb.Append($p.Substring(1)) | Out-Null
            }
        }
    }
    return $sb.ToString()
}

function Get-CandidateCsFile($crate, $relPath) {
    if ($crate -eq 'wacore/binary') {
        if ($relPath -match 'decoder') { return 'wacore/binary/BinaryDecoder.cs' }
        if ($relPath -match 'encoder') { return 'wacore/binary/BinaryEncoder.cs' }
        if ($relPath -match 'jid') { return 'wacore/binary/Jid.cs' }
        if ($relPath -match 'node') { return 'wacore/binary/Node.cs' }
        if ($relPath -match 'token') { return 'wacore/binary/TokenDictionary.cs' }
        if ($relPath -match 'framing') { return 'wacore/binary/Framing.cs' }
        if ($relPath -match 'marshal|util|zlib') { return 'wacore/binary/BinaryPacker.cs' }
        if ($relPath -match 'const') { return 'wacore/binary/Constants.cs' }
        return 'wacore/binary/Node.cs'
    }
    if ($crate -eq 'wacore/noise') {
        if ($relPath -match 'curve') { return 'wacore/noise/Curve25519.cs' }
        if ($relPath -match 'handshake|message') { return 'wacore/noise/HandshakeMessages.cs' }
        if ($relPath -match 'ik') { return 'wacore/noise/IkHandshake.cs' }
        if ($relPath -match 'cipher') { return 'wacore/noise/NoiseCipher.cs' }
        if ($relPath -match 'state') { return 'wacore/noise/NoiseState.cs' }
        if ($relPath -match 'xed|eddsa') { return 'wacore/noise/XEdDSA.cs' }
        if ($relPath -match 'xx_fallback') { return 'wacore/noise/XxFallbackHandshake.cs' }
        if ($relPath -match 'xx') { return 'wacore/noise/XxHandshake.cs' }
        return 'wacore/noise/NoiseCipher.cs'
    }
    if ($crate -eq 'wacore/libsignal') {
        if ($relPath -match 'curve') { return 'wacore/noise/Curve25519.cs' }
        if ($relPath -match 'cipher|ratchet') { return 'wacore/libsignal/SignalCipher.cs' }
        if ($relPath -match 'message|protocol') { return 'wacore/libsignal/SignalMessages.cs' }
        if ($relPath -match 'store') { return 'storages/sqlite-storage/DeviceStore.cs' }
        return 'wacore/libsignal/SignalCipher.cs'
    }
    if ($crate -eq 'waproto') {
        if ($relPath -match 'adv') { return 'waproto/AdvProto.cs' }
        if ($relPath -match 'history_sync') { return 'waproto/HistorySyncProto.cs' }
        if ($relPath -match 'payload') { return 'waproto/ClientPayload.cs' }
        return 'waproto/WaCodec.cs'
    }
    if ($crate -eq 'storages/sqlite-storage') {
        if ($relPath -match 'cred') { return 'storages/sqlite-storage/DeviceCredentials.cs' }
        return 'storages/sqlite-storage/DeviceStore.cs'
    }
    if ($crate -eq 'transports/tokio-transport') {
        return 'transports/socket-transport/WhatsAppSocket.cs'
    }
    if ($crate -eq 'http_clients/ureq-client') {
        return 'http_clients/http-client/HistorySyncDownloader.cs'
    }
    if ($crate -eq 'src') {
        if ($relPath -match '(?:^|/)pair(?:\.rs|/)') { return 'src/WhatsApp.Client/Pairing/PairCrypto.cs' }
        if ($relPath -match '(?:^|/)client(?:\.rs|/)') { return 'src/WhatsApp.Client/WhatsAppClient.cs' }
        return $null
    }
    if ($crate -eq 'wacore') {
        if ($relPath -match '(?:^|/)pair(?:\.rs|/)') { return 'src/WhatsApp.Client/Pairing/PairCrypto.cs' }
        if ($relPath -match '(?:^|/)client(?:\.rs|/)') { return 'src/WhatsApp.Client/WhatsAppClient.cs' }
        if ($relPath -match 'socket|transport') { return 'transports/socket-transport/WhatsAppSocket.cs' }
        return $null
    }
    return $null
}

function Match-CsMember($candidateFile, $fnName) {
    if (-not $candidateFile -or -not $csIndex.ContainsKey($candidateFile)) {
        return $null
    }
    $members = $csIndex[$candidateFile]
    $pascal = To-PascalCase $fnName
    
    if ($members.Contains($fnName)) { return $fnName }
    if ($members.Contains($pascal)) { return $pascal }
    if ($members.Contains($pascal + "Async")) { return ($pascal + "Async") }
    
    if ($fnName -eq 'new') {
        $className = [System.IO.Path]::GetFileNameWithoutExtension($candidateFile)
        if ($members.Contains($className)) { return $className }
    }
    if ($fnName -eq 'parse' -and $members.Contains('Parse')) { return 'Parse' }
    if ($fnName -eq 'to_string' -and $members.Contains('ToString')) { return 'ToString' }
    if ($fnName -eq 'sign' -and $members.Contains('Sign')) { return 'Sign' }
    if ($fnName -eq 'verify' -and $members.Contains('Verify')) { return 'Verify' }
    if ($fnName -eq 'encrypt' -and $members.Contains('Encrypt')) { return 'Encrypt' }
    if ($fnName -eq 'decrypt' -and $members.Contains('Decrypt')) { return 'Decrypt' }
    if ($fnName -eq 'connect' -and $members.Contains('ConnectAsync')) { return 'ConnectAsync' }
    if ($fnName -eq 'disconnect' -and $members.Contains('DisconnectAsync')) { return 'DisconnectAsync' }
    if ($fnName -eq 'logout' -and $members.Contains('LogoutAsync')) { return 'LogoutAsync' }
    
    return $null
}

Write-Host "Scanning upstream Rust codebase..." -ForegroundColor Yellow
$sw = [System.Diagnostics.Stopwatch]::StartNew()

$crateData = [System.Collections.Generic.Dictionary[string, PSCustomObject]]::new()
$totalFileCount = 0
$totalFnCount = 0
$totalPubCount = 0
$totalIntCount = 0
$totalPrivCount = 0

$totalCompleteFns = 0
$totalInProgressFns = 0
$totalNotStartedFns = 0

$totalCompletePub = 0
$totalInProgressPub = 0
$totalNotStartedPub = 0

$totalCompleteInt = 0
$totalInProgressInt = 0
$totalNotStartedInt = 0

$totalCompletePriv = 0
$totalInProgressPriv = 0
$totalNotStartedPriv = 0

$totalCompleteFiles = 0
$totalInProgressFiles = 0
$totalNotStartedFiles = 0

foreach ($c in $crates) {
    $p = if ($c -eq 'src') { Join-Path $ReferenceRoot 'src' } else { Join-Path $ReferenceRoot ($c -replace '/', '\') }
    if (-not (Test-Path $p)) {
        Write-Warning "Crate directory not found: $p"
        continue
    }
    
    $files = Get-ChildItem -Path $p -Filter "*.rs" -Recurse | Where-Object { 
        -not (Is-TestFile $_.FullName $c)
    } | Sort-Object FullName
    
    $fileList = [System.Collections.Generic.List[PSCustomObject]]::new()
    $crateTotalPub = 0
    $crateTotalInt = 0
    $crateTotalPriv = 0
    $crateComplete = 0
    $crateInProgress = 0
    $crateNotStarted = 0
    
    foreach ($f in $files) {
        $relPath = $f.FullName.Substring($ReferenceRoot.Length).TrimStart('\', '/') -replace '\\', '/'
        $fns = @(Parse-RustProductionFunctions $f.FullName)
        $candFile = Get-CandidateCsFile $c $relPath
        
        $filePub = 0
        $fileInt = 0
        $filePriv = 0
        $fileComplete = 0
        $fileInProgress = 0
        $fileNotStarted = 0
        
        $fnList = [System.Collections.Generic.List[PSCustomObject]]::new()
        
        foreach ($fn in $fns) {
            $vis = $fn.Visibility
            $dvis = $fn.DisplayVis
            
            if ($vis -eq 'pub') { $filePub++ }
            elseif ($vis -eq 'internal') { $fileInt++ }
            else { $filePriv++ }
            
            $matchedMember = Match-CsMember $candFile $fn.Name
            
            if ($matchedMember) {
                $status = 'Complete'
                $targetCsFile = $candFile
                $targetCsSym = "$($csClassMap[$candFile]).$matchedMember"
                $notes = "Ported to $targetCsSym in WhatsAppWinUI"
                $fileComplete++
                
                if ($vis -eq 'pub') { $totalCompletePub++ }
                elseif ($vis -eq 'internal') { $totalCompleteInt++ }
                else { $totalCompletePriv++ }
            } else {
                $status = 'Not Started'
                $targetCsFile = '-'
                $targetCsSym = '-'
                $notes = 'Pending port'
                $fileNotStarted++
                
                if ($vis -eq 'pub') { $totalNotStartedPub++ }
                elseif ($vis -eq 'internal') { $totalNotStartedInt++ }
                else { $totalNotStartedPriv++ }
            }
            
            $fnList.Add([PSCustomObject]@{
                Line = $fn.Line
                Name = $fn.Name
                Visibility = $dvis
                RawVis = $vis
                TargetCs = $targetCsFile
                TargetSym = $targetCsSym
                Status = $status
                Notes = $notes
            })
        }
        
        # Determine overall file status and target C# file
        if ($fns.Length -eq 0) {
            # File with 0 production functions
            $fileStatus = 'Not Started'
            $fileTargetCs = '-'
            $totalNotStartedFiles++
        } else {
            if ($fileComplete -gt 0 -and $fileNotStarted -eq 0) {
                $fileStatus = 'Complete'
                $fileTargetCs = $candFile
                $totalCompleteFiles++
            } elseif ($fileComplete -gt 0 -and $fileNotStarted -gt 0) {
                $fileStatus = 'In Progress'
                $fileTargetCs = $candFile
                $totalInProgressFiles++
            } else {
                $fileStatus = 'Not Started'
                $fileTargetCs = '-'
                $totalNotStartedFiles++
            }
        }
        
        $crateTotalPub += $filePub
        $crateTotalInt += $fileInt
        $crateTotalPriv += $filePriv
        $crateComplete += $fileComplete
        $crateInProgress += $fileInProgress
        $crateNotStarted += $fileNotStarted
        
        $fileList.Add([PSCustomObject]@{
            RelativePath = $relPath
            PubIntCount = ($filePub + $fileInt)
            TotalCount = $fns.Length
            TargetCsFile = $fileTargetCs
            FileStatus = $fileStatus
            Functions = $fnList
        })
    }
    
    $crateTotalProd = ($crateTotalPub + $crateTotalInt + $crateTotalPriv)
    $totalFileCount += $fileList.Count
    $totalFnCount += $crateTotalProd
    $totalPubCount += $crateTotalPub
    $totalIntCount += $crateTotalInt
    $totalPrivCount += $crateTotalPriv
    $totalCompleteFns += $crateComplete
    $totalInProgressFns += $crateInProgress
    $totalNotStartedFns += $crateNotStarted
    
    $cratePct = if ($crateTotalProd -gt 0) { [math]::Round(($crateComplete / $crateTotalProd) * 100, 1) } else { 0.0 }
    
    $crateData[$c] = [PSCustomObject]@{
        Crate = $c
        Package = $cratePackages[$c]
        Project = $crateProjects[$c]
        ProjectPath = $crateProjectPaths[$c]
        Status = $crateStatuses[$c]
        Notes = $crateNotes[$c]
        Files = $fileList
        TotalPub = $crateTotalPub
        TotalInt = $crateTotalInt
        TotalPriv = $crateTotalPriv
        TotalProd = $crateTotalProd
        Complete = $crateComplete
        InProgress = $crateInProgress
        NotStarted = $crateNotStarted
        PctComplete = $cratePct
    }
}
$sw.Stop()

Write-Host ("Scanned {0} crates, {1} files, {2} functions in {3} ms" -f $crateData.Count, $totalFileCount, $totalFnCount, $sw.ElapsedMilliseconds) -ForegroundColor Green
Write-Host ("Complete: {0}, In Progress: {1}, Not Started: {2}" -f $totalCompleteFns, $totalInProgressFns, $totalNotStartedFns)

# Roll up workspace totals
$wsPctComplete = if ($totalFnCount -gt 0) { [math]::Round(($totalCompleteFns / $totalFnCount) * 100, 1) } else { 0.0 }
$pubPctComplete = if ($totalPubCount -gt 0) { [math]::Round(($totalCompletePub / $totalPubCount) * 100, 1) } else { 0.0 }
$intPctComplete = if ($totalIntCount -gt 0) { [math]::Round(($totalCompleteInt / $totalIntCount) * 100, 1) } else { 0.0 }
$privPctComplete = if ($totalPrivCount -gt 0) { [math]::Round(($totalCompletePriv / $totalPrivCount) * 100, 1) } else { 0.0 }
$filePctComplete = if ($totalFileCount -gt 0) { [math]::Round(($totalCompleteFiles / $totalFileCount) * 100, 1) } else { 0.0 }

$crateCompleteCount = ($crateStatuses.Values | Where-Object { $_ -eq 'Complete' }).Count
$crateInProgressCount = ($crateStatuses.Values | Where-Object { $_ -eq 'In Progress' }).Count
$crateNotStartedCount = ($crateStatuses.Values | Where-Object { $_ -eq 'Not Started' }).Count
$cratePctComplete = [math]::Round(($crateCompleteCount / 11) * 100, 1)

# Write Markdown to OutputFile using StreamWriter for maximum performance
Write-Host "Writing markdown documentation to: $OutputFile..." -ForegroundColor Yellow
$writer = [System.IO.StreamWriter]::new($OutputFile, $false, [System.Text.Encoding]::UTF8)

# Title & Description
$writer.WriteLine("# WhatsAppWinUI Port Progress & Inventory")
$writer.WriteLine("")
$writer.WriteLine("> Single source of truth for the 1:1 port of " + $bt + "oxidezap/whatsapp-rust" + $bt + " to native .NET 9 / WinUI 3 in " + $bt + "WhatsAppWinUI" + $bt + ".")
$writer.WriteLine("> Tracks 100% of upstream production crates (11), source files (" + $totalFileCount + "), and production functions/APIs cross-referenced against C# implementations.")
$writer.WriteLine("")
$writer.WriteLine("---")
$writer.WriteLine("")

# Section 1: Executive Dashboard
$writer.WriteLine("## Executive Dashboard")
$writer.WriteLine("")
$writer.WriteLine("### Overall Metrics")
$writer.WriteLine("| Metric | Total Count | Complete | In Progress | Not Started | % Complete |")
$writer.WriteLine("| --- | --- | --- | --- | --- | --- |")
$writer.WriteLine("| Total Upstream Crates | 11 | " + $crateCompleteCount + " | " + $crateInProgressCount + " | " + $crateNotStartedCount + " | " + $cratePctComplete + "% |")
$writer.WriteLine("| Total Production Files (.rs) | " + $totalFileCount + " | " + $totalCompleteFiles + " | " + $totalInProgressFiles + " | " + $totalNotStartedFiles + " | " + $filePctComplete + "% |")
$writer.WriteLine("| Total Production Functions / APIs | " + $totalFnCount + " | " + $totalCompleteFns + " | " + $totalInProgressFns + " | " + $totalNotStartedFns + " | " + $wsPctComplete + "% |")
$writer.WriteLine("| Public APIs (" + $bt + "pub" + $bt + ") | " + $totalPubCount + " | " + $totalCompletePub + " | " + $totalInProgressPub + " | " + $totalNotStartedPub + " | " + $pubPctComplete + "% |")
$writer.WriteLine("| Internal APIs (" + $bt + "pub(...)" + $bt + ") | " + $totalIntCount + " | " + $totalCompleteInt + " | " + $totalInProgressInt + " | " + $totalNotStartedInt + " | " + $intPctComplete + "% |")
$writer.WriteLine("| Private Functions (" + $bt + "fn" + $bt + ") | " + $totalPrivCount + " | " + $totalCompletePriv + " | " + $totalInProgressPriv + " | " + $totalNotStartedPriv + " | " + $privPctComplete + "% |")
$writer.WriteLine("")

$writer.WriteLine("### Crate Roll-Up Summary")
$writer.WriteLine("| Upstream Crate | Upstream Package Name | Source Files | Pub/Internal Functions | Private Functions | Total Functions | Complete | In Progress | Not Started | % Complete |")
$writer.WriteLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |")
foreach ($c in $crates) {
    $cd = $crateData[$c]
    $row = "| " + $bt + $cd.Crate + $bt + " | " + $bt + $cd.Package + $bt + " | " + $cd.Files.Count + " | " + ($cd.TotalPub + $cd.TotalInt) + " | " + $cd.TotalPriv + " | " + $cd.TotalProd + " | " + $cd.Complete + " | " + $cd.InProgress + " | " + $cd.NotStarted + " | " + $cd.PctComplete + "% |"
    $writer.WriteLine($row)
}
$totRow = "| **Total** | **11 Crates** | **" + $totalFileCount + "** | **" + ($totalPubCount + $totalIntCount) + "** | **" + $totalPrivCount + "** | **" + $totalFnCount + "** | **" + $totalCompleteFns + "** | **" + $totalInProgressFns + "** | **" + $totalNotStartedFns + "** | **" + $wsPctComplete + "%** |"
$writer.WriteLine($totRow)
$writer.WriteLine("")
$writer.WriteLine("---")
$writer.WriteLine("")

# Section 2: Crate Directory Mapping
$writer.WriteLine("## Crate Directory Mapping")
$writer.WriteLine("")
$writer.WriteLine("| Upstream Crate Path | Upstream Package Name | C# Project Path | Target .csproj | Status | Architecture Notes |")
$writer.WriteLine("| --- | --- | --- | --- | --- | --- |")
foreach ($c in $crates) {
    $cd = $crateData[$c]
    $row = "| " + $bt + $cd.Crate + $bt + " | " + $bt + $cd.Package + $bt + " | " + $bt + $cd.ProjectPath + $bt + " | " + $bt + $cd.Project + $bt + " | " + $cd.Status + " | " + $cd.Notes + " |"
    $writer.WriteLine($row)
}
$writer.WriteLine("")
$writer.WriteLine("---")
$writer.WriteLine("")

# Section 3: Per-Crate Inventories
$writer.WriteLine("## Per-Crate Inventories")
$writer.WriteLine("")

foreach ($c in $crates) {
    $cd = $crateData[$c]
    Write-Host ("Formatting Crate: " + $c + " (" + $cd.Files.Count + " files, " + $cd.TotalProd + " functions)...")
    
    $writer.WriteLine("## Crate: " + $cd.Crate)
    $writer.WriteLine("- **Package**: " + $bt + $cd.Package + $bt)
    $writer.WriteLine("- **Target C# Project**: " + $bt + $cd.Project + $bt)
    $writer.WriteLine("- **Source Files**: " + $cd.Files.Count)
    $writer.WriteLine("- **Total Production Functions**: " + $cd.TotalProd + " (Pub: " + $cd.TotalPub + ", Internal: " + $cd.TotalInt + ", Private: " + $cd.TotalPriv + ")")
    $writer.WriteLine("")
    
    # File Summary Table
    $writer.WriteLine("### File Summary: " + $cd.Crate)
    $writer.WriteLine("| Upstream File | Pub/Internal Functions | Total Functions | Target C# File | File Status |")
    $writer.WriteLine("| --- | --- | --- | --- | --- |")
    foreach ($file in $cd.Files) {
        $csTargetCol = if ($file.TargetCsFile -eq '-') { '-' } else { $bt + $file.TargetCsFile + $bt }
        $fRow = "| " + $bt + $file.RelativePath + $bt + " | " + $file.PubIntCount + " | " + $file.TotalCount + " | " + $csTargetCol + " | " + $file.FileStatus + " |"
        $writer.WriteLine($fRow)
    }
    $writer.WriteLine("")
    
    # Detailed Function Inventory Table
    $writer.WriteLine("### Function Inventory: " + $cd.Crate)
    $writer.WriteLine("| Upstream File | Function / API Symbol | Visibility | Target C# File | Target C# Symbol | Status | Notes |")
    $writer.WriteLine("| --- | --- | --- | --- | --- | --- | --- |")
    foreach ($file in $cd.Files) {
        foreach ($fn in $file.Functions) {
            $csFileCol = if ($fn.TargetCs -eq '-') { '-' } else { $bt + $fn.TargetCs + $bt }
            $csSymCol = if ($fn.TargetSym -eq '-') { '-' } else { $bt + $fn.TargetSym + $bt }
            $fnRow = "| " + $bt + $file.RelativePath + $bt + " | " + $bt + $fn.Name + $bt + " | " + $fn.Visibility + " | " + $csFileCol + " | " + $csSymCol + " | " + $fn.Status + " | " + $fn.Notes + " |"
            $writer.WriteLine($fnRow)
        }
    }
    $writer.WriteLine("")
}

$writer.Flush()
$writer.Close()

Write-Host ("Successfully generated " + $OutputFile + "!") -ForegroundColor Green
