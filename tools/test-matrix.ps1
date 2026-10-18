<#
  P3.4 helper. Run on each machine in the test matrix (Windows 11, Server 2016, 2019, 2022, 2025),
  once as a standard user and once elevated. It prints a block to paste into tracker.md.

  Usage:   powershell -ExecutionPolicy Bypass -File test-matrix.ps1 -Exe .\PatchRadar-v1.0.0-win-x64.exe
  Optional: -SignedFile <path to a file with an embedded Authenticode signature>
            (default: dotnet.exe if present; on a signed PatchRadar release pass the release exe)

  What it checks (nothing is installed; PatchRadar's real settings, state and Run key are not touched):
    1. OS, build, edition, elevation
    2. PatchRadar --diag: WUA history, QFE, merge time, live KB fetch, release health page for this OS
    3. Renaming and replacing a RUNNING exe (the self-update mechanism), using a copy of ping.exe
    4. WinVerifyTrust through PatchRadar --verify on a signed file (SHA-256 / timestamped signatures)
    5. Which .NET runtimes exist (matters for the slim build)
#>
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [string]$SignedFile
)

$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path $Exe).Path
$work = Join-Path $env:TEMP ("patchradar-matrix-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory $work | Out-Null
$notes = New-Object System.Collections.Generic.List[string]
$ok = $true

# ---- 1. OS ------------------------------------------------------------------------------------
$cv = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
$elevated = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$product = $cv.ProductName
if ([int]$cv.CurrentBuildNumber -ge 22000 -and $product -like 'Windows 10*') { $product = $product -replace 'Windows 10', 'Windows 11' }   # the registry still says 10
$os = "$product $($cv.DisplayVersion) build $($cv.CurrentBuildNumber).$($cv.UBR)"
Write-Host "OS: $os   user: $env:USERNAME   elevated: $elevated"

# ---- 2. --diag --------------------------------------------------------------------------------
$diagFile = Join-Path $work 'diag.txt'
$proc = Start-Process $Exe -ArgumentList @('--diag', "`"$diagFile`"", 'KB5127648', '--webview') -PassThru -WindowStyle Hidden
if (-not $proc.WaitForExit(180000)) { $proc.Kill(); $notes.Add('diag timed out after 180 s'); $ok = $false }
$diag = if (Test-Path $diagFile) { Get-Content $diagFile } else { @() }
$wua = $diag | Where-Object { $_ -like 'WUA:*' }
$qfe = $diag | Where-Object { $_ -like 'QFE:*' }
$col = $diag | Where-Object { $_ -like 'Collector:*' }
$fetch = $diag | Where-Object { $_ -like 'Fetch KB5127648*' }
$rh = $diag | Where-Object { $_ -like '*release health KB*' }
$pages = $diag | Where-Object { $_ -like 'OS:*' }
$web = $diag | Where-Object { $_ -like '*WebView2*' }
if (-not $diag) { $notes.Add('diag produced no output'); $ok = $false }
if ($wua -match 'FAILED') { $notes.Add('WUA FAILED'); $ok = $false }
if ($qfe -match 'FAILED') { $notes.Add('QFE FAILED'); $ok = $false }
if ($fetch -notmatch 'status=(Ok|NotFound)') { $notes.Add('KB fetch not Ok'); $ok = $false }
$diag | ForEach-Object { Write-Host "  $_" }

# ---- 3. rename a running exe ------------------------------------------------------------------
$victim = Join-Path $work 'victim.exe'
Copy-Item "$env:SystemRoot\System32\ping.exe" $victim
$run = Start-Process $victim -ArgumentList @('-n', '40', '127.0.0.1') -PassThru -WindowStyle Hidden
Start-Sleep -Seconds 2
$renameOk = $false
try {
    Move-Item $victim "$victim.old" -ErrorAction Stop
    Copy-Item "$victim.old" $victim -ErrorAction Stop
    $renameOk = $true
} catch { $notes.Add("rename-running-exe FAILED: $($_.Exception.Message)"); $ok = $false }
$deleteBlocked = $false
try { Remove-Item "$victim.old" -ErrorAction Stop } catch { $deleteBlocked = $true }
Stop-Process $run.Id -Force -ErrorAction SilentlyContinue
Write-Host "rename running exe: $renameOk (deleting it while running blocked: $deleteBlocked)"

# ---- 4. WinVerifyTrust --------------------------------------------------------------------------
if (-not $SignedFile) {
    $SignedFile = @("$env:ProgramFiles\dotnet\dotnet.exe", "${env:ProgramFiles(x86)}\dotnet\dotnet.exe") | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
}
$verify = 'skipped (no signed file found)'
if ($SignedFile -and (Test-Path $SignedFile)) {
    $vf = Join-Path $work 'verify.txt'
    $vp = Start-Process $Exe -ArgumentList @('--verify', "`"$vf`"", "`"$SignedFile`"") -PassThru -WindowStyle Hidden
    [void]$vp.WaitForExit(60000)
    $verify = if (Test-Path $vf) { (Get-Content $vf -Raw).Trim() } else { 'no output' }
    $verifyStatus = $verify.Split('|')[0]
    if ($verifyStatus -notin @('Valid', 'WrongSigner')) { $notes.Add("WinVerifyTrust gave $verifyStatus on $SignedFile"); $ok = $false }
}
Write-Host "signature check on ${SignedFile}: $verify"

# ---- 5. .NET runtimes ---------------------------------------------------------------------------
$runtimes = try {
    $r = & dotnet --list-runtimes 2>$null | Where-Object { $_ -match '^Microsoft\.(NETCore|WindowsDesktop)\.App' } | ForEach-Object { ($_ -split ' ')[0].Replace('Microsoft.', '').Replace('.App', '') + ' ' + ($_ -split ' ')[1] }
    if ($r) { $r -join ', ' } else { 'none (fine for the self-contained build)' }
} catch { 'dotnet not on PATH (fine for the self-contained build)' }
Write-Host ".NET runtimes: $runtimes"

# ---- result block -------------------------------------------------------------------------------
$role = if ($elevated) { 'Admin' } else { 'Std user' }
$mark = if ($ok) { 'PASS' } else { 'FAIL' }
$summary = ("$mark ($role): $($wua -replace 'WUA: ','WUA '), $($col -replace 'Collector: ',''), rename-running-exe=$renameOk, signature=$($verify.Split('|')[0])").Trim()
if ($notes.Count) { $summary += ' | ' + ($notes -join '; ') }

Write-Host ''
Write-Host '---- paste into tracker.md (test matrix, P3.4) ----'
Write-Host "Machine: $os"
Write-Host "Column:  $role"
Write-Host "Result:  $summary"
Write-Host '---- paste into tracker.md (unverified items log, where relevant) ----'
Write-Host "Release health pages: $pages"
Write-Host "Release health fetch: $rh"
Write-Host "WinVerifyTrust: $verify"
Write-Host "WebView2: $web"
Write-Host ".NET: $runtimes"

Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
if (-not $ok) { exit 1 }
