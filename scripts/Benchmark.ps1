param([string]$Filter = '*')
$ErrorActionPreference = 'Stop'
Push-Location (Join-Path $PSScriptRoot '..')
try {
    dotnet run --project benchmarks/Tedd.Defrag.Benchmarks -c Release -- --filter $Filter --artifacts artifacts/benchmarks
    if ($LASTEXITCODE -ne 0) { throw 'Benchmark run failed.' }
} finally { Pop-Location }
