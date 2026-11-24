$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$docsDir = Join-Path $repoRoot "docs\whatsapp-rust-docs"
if (-not (Test-Path $docsDir)) { New-Item -ItemType Directory -Path $docsDir -Force | Out-Null }

$wc = New-Object System.Net.WebClient
$wc.Headers.Add("User-Agent", "Mozilla/5.0")

Write-Host "Downloading llms-full.txt..."
$wc.DownloadFile("https://whatsapp-rust.jlucaso.com/llms-full.txt", "$docsDir\llms-full.txt")
Write-Host "llms-full.txt downloaded."

Write-Host "Downloading llms.txt..."
$llmsText = $wc.DownloadString("https://whatsapp-rust.jlucaso.com/llms.txt")
[System.IO.File]::WriteAllText("$docsDir\llms.txt", $llmsText)

$regex = [regex]'- \[[^\]]+\]\((https://whatsapp-rust\.jlucaso\.com/([^\)]+\.md))\)'
$matches = $regex.Matches($llmsText)
Write-Host "Found $($matches.Count) doc pages in llms.txt"

foreach ($m in $matches) {
    $url = $m.Groups[1].Value
    $relPath = $m.Groups[2].Value.Replace('/', '\')
    $targetFile = Join-Path $docsDir $relPath
    $targetFolder = Split-Path $targetFile -Parent
    if (-not (Test-Path $targetFolder)) { New-Item -ItemType Directory -Path $targetFolder -Force | Out-Null }
    
    try {
        $wc.DownloadFile($url, $targetFile)
        Write-Host "Downloaded: $relPath"
    } catch {
        Write-Host "Failed: $url"
    }
}
Write-Host "Finished downloading docs."
$count = (Get-ChildItem -Path $docsDir -Recurse -File).Count
Write-Host "Total files downloaded: $count"
