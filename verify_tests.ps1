# Comprehensive test verification script
cd "C:\Users\madsk\GitHub\Macros"

$isolationPass = 0
$isolationFail = 0
$suitePass = 0
$suiteFail = 0
$p95Values = @()

Write-Host "=== COMPREHENSIVE TEST VERIFICATION ===" -ForegroundColor Yellow
Write-Host ""
Write-Host "--- PHASE 1: Isolation Runs (5x) ---" -ForegroundColor Cyan
Write-Host ""

for ($i = 1; $i -le 5; $i++) {
    $testName = "Macros.Tests.Performance.PerformanceBenchmarks.MacroEventBus_Subscribe_FirstTime_Under_5ms"
    $output = @(dotnet test tests\Macros.Tests --filter "FullyQualifiedName=$testName" --no-build 2>&1)
    
    $status = "PASS"
    if ($output -join "`n" | Select-String "FAILED") {
        $status = "FAIL"
        $isolationFail++
    } else {
        $isolationPass++
    }
    Write-Host "Isolation run $i`: $status"
}

Write-Host ""
Write-Host "--- PHASE 2: Full Suite Runs (5x) ---" -ForegroundColor Cyan
Write-Host ""

for ($i = 1; $i -le 5; $i++) {
    $output = @(dotnet test tests\Macros.Tests --no-build 2>&1)
    $fullText = $output -join "`n"
    
    if ($fullText | Select-String "FAILED") {
        Write-Host "Full run $i`: FAIL"
        $suiteFail++
        
        # Try to extract P95
        if ($fullText -match "P95.*exceeds") {
            $match = [regex]::Match($fullText, "P95=([0-9.]+)")
            if ($match.Success) {
                $p95Val = [double]$match.Groups[1].Value
                $p95Values += $p95Val
                Write-Host "  P95: $p95Val ms"
            }
        }
    } else {
        Write-Host "Full run $i`: PASS"
        $suitePass++
    }
}

Write-Host ""
Write-Host "=== SUMMARY ===" -ForegroundColor Yellow
Write-Host "Isolation runs: $isolationPass pass, $isolationFail fail (5 total)"
Write-Host "Full-suite runs: $suitePass pass, $suiteFail fail (5 total)"

if ($p95Values.Count -gt 0) {
    $sorted = @($p95Values | Sort-Object)
    Write-Host ""
    Write-Host "P95 Timing Observations:"
    Write-Host "  Min: $($sorted[0]) ms"
    if ($sorted.Count -gt 1) {
        Write-Host "  Median: $($sorted[[int]($sorted.Count/2)]) ms"
    }
    Write-Host "  Max: $($sorted[-1]) ms"
    Write-Host "  Threshold: 5 ms"
}

Write-Host ""
if ($suiteFail -gt 0 -and $isolationPass -eq 5) {
    Write-Host "VERDICT: FLAKY - intermittent in full suite, consistent in isolation" -ForegroundColor Green
} elseif ($suiteFail -gt 0 -and $isolationFail -gt 0) {
    Write-Host "VERDICT: GENUINE REGRESSION - fails in both contexts" -ForegroundColor Red
} else {
    Write-Host "VERDICT: UNCLEAR - inconsistent patterns" -ForegroundColor Yellow
}
