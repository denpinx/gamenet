# 一键构建 + 测试 + 基准
# 用法：pwsh -File scripts/run-all.ps1 [-SkipBench]

param(
    [switch]$SkipBench
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    Write-Host '== build ==' -ForegroundColor Cyan
    dotnet build GameNet.sln -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'build failed' }

    Write-Host '== test ==' -ForegroundColor Cyan
    $results = Join-Path $root 'TestResults'
    Remove-Item $results -Recurse -Force -ErrorAction SilentlyContinue
    dotnet test tests/GameNet.Tests/GameNet.Tests.csproj --nologo -v q --collect:"XPlat Code Coverage" --results-directory $results
    if ($LASTEXITCODE -ne 0) { throw 'tests failed' }

    $coverageXml = Get-ChildItem $results -Recurse -Filter coverage.cobertura.xml |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $coverageXml) { throw 'coverage report missing' }
    [xml]$coverage = Get-Content $coverageXml.FullName
    $lineRate = [double]$coverage.coverage.'line-rate'
    $branchRate = [double]$coverage.coverage.'branch-rate'
    Write-Host ("coverage: lines={0:P1} branches={1:P1}" -f $lineRate, $branchRate)
    if ($lineRate -lt 0.75 -or $branchRate -lt 0.60) {
        throw ("coverage gate failed: need 75% lines / 60% branches, got {0:P1} / {1:P1}" -f $lineRate, $branchRate)
    }

    if (-not $SkipBench) {
        Write-Host '== benchmark ==' -ForegroundColor Cyan
        dotnet run --project src/GameNet.Benchmarks -c Release
        if ($LASTEXITCODE -ne 0) { throw 'benchmark failed' }
    }

    Write-Host 'done' -ForegroundColor Green
}
finally {
    Pop-Location
}
