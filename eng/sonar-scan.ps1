#Requires -Version 7.0
<#
.SYNOPSIS
    Runs one full SonarCloud analysis of SharpCoreAgent, with coverage.

.DESCRIPTION
    The default way to scan. `dotnet build`/`dotnet test` alone do not analyse anything: the scanner
    injects its Roslyn analyzers between `begin` and `end`, which is why this is one command instead of
    a set of `-p:` properties on a normal build.

    Every analysis setting lives here and nowhere else. There is no `SonarQube.Analysis.xml` and no
    `sonar-project.properties` in this repository: the .NET scanner does not read the latter, and its
    settings-file format is not worth a second copy of settings that would drift from this one. CI runs
    this exact script (see .github/workflows/sonar.yml), so a scan on a laptop and a scan on a runner
    cannot disagree about exclusions or coverage paths.

.PARAMETER Token
    SonarCloud token. Defaults to the SONAR_TOKEN environment variable, which is what CI provides.

.PARAMETER Configuration
    Build configuration. Release by default: coverage of a Debug build is still coverage, but `end`
    publishes metrics for whatever was compiled, and Release is what ships.

.PARAMETER WaitForQualityGate
    Fail with a non-zero exit code when the quality gate fails. Use in CI and only there: on a laptop a
    red gate should print, not stop you from running a scan.

.NOTES
    Coverage comes from Microsoft.Testing.Platform's own coverage extension (`--coverage`), not from a
    VSTest data collector, and it lands in TestResults/sonar/coverage.cobertura.xml.
#>
[CmdletBinding()]
param(
    [string] $Token = $env:SONAR_TOKEN,
    [string] $Configuration = 'Release',
    [switch] $WaitForQualityGate
)

$ErrorActionPreference = 'Stop'

$ProjectKey = 'MPCoreDeveloper_SharpCoreAgent'
$Organization = 'mpcoredeveloper'
$CoveragePath = 'TestResults/sonar/coverage.cobertura.xml'
$Solution = 'SharpCoreAgent.slnx'

if ([string]::IsNullOrWhiteSpace($Token)) {
    throw 'No SonarCloud token. Set SONAR_TOKEN or pass -Token.'
}

$root = Split-Path -Parent $PSScriptRoot
Push-Location $root

try {
    # A stale report is worse than none: `end` would publish yesterday's coverage as today's.
    Remove-Item TestResults -Recurse -Force -ErrorAction SilentlyContinue

    $beginArguments = @(
        'sonarscanner', 'begin'
        "/k:$ProjectKey"
        "/o:$Organization"
        '/d:sonar.host.url=https://sonarcloud.io'
        "/d:sonar.token=$Token"
        '/d:sonar.sourceEncoding=UTF-8'
        "/d:sonar.cs.cobertura.reportsPaths=$CoveragePath"
        '/d:sonar.exclusions=**/bin/**,**/obj/**,**/*.g.cs,**/*.generated.cs,**/*.Designer.cs'
        '/d:sonar.coverage.exclusions=tests/**'
    )

    if ($WaitForQualityGate) {
        $beginArguments += '/d:sonar.qualitygate.wait=true'
    }

    Write-Host '--- sonarscanner begin' -ForegroundColor Cyan
    & dotnet @beginArguments
    if ($LASTEXITCODE -ne 0) { throw "sonarscanner begin failed ($LASTEXITCODE)." }

    # The analyzers only run on a build they can see, and an up-to-date build is skipped entirely.
    Write-Host '--- build' -ForegroundColor Cyan
    & dotnet build $Solution -c $Configuration --no-incremental --nologo
    if ($LASTEXITCODE -ne 0) { throw "build failed ($LASTEXITCODE)." }

    Write-Host '--- test with coverage' -ForegroundColor Cyan
    & dotnet test $Solution -c $Configuration --no-build --coverage --coverage-output-format cobertura --coverage-output sonar/coverage.cobertura.xml
    if ($LASTEXITCODE -ne 0) { throw "tests failed ($LASTEXITCODE)." }

    if (-not (Test-Path $CoveragePath)) {
        throw "No coverage report at $CoveragePath; Sonar would publish 0%."
    }

    Write-Host '--- sonarscanner end' -ForegroundColor Cyan
    & dotnet sonarscanner end "/d:sonar.token=$Token"
    if ($LASTEXITCODE -ne 0) { throw "sonarscanner end failed ($LASTEXITCODE)." }

    Write-Host ''
    Write-Host "Dashboard: https://sonarcloud.io/dashboard?id=$ProjectKey" -ForegroundColor Green
}
finally {
    Pop-Location
}
