# Transitive dependency audit (DEPENDENCY_POLICY.md, licensing note).
# The build-time ForbiddenPackagesTests govern DIRECT references; this script walks
# the full resolved graph so a banned package can't arrive as someone's dependency.
# Run from the repository root. Exits non-zero on any hit.
$ErrorActionPreference = 'Stop'

$bannedPrefixes = @(
    'MediatR',
    'AutoMapper',
    'Newtonsoft.Json',
    'FluentAssertions',
    'Moq',
    'Hangfire',
    'MassTransit'
)

# Documented exemptions: banned ids that are known to arrive ONLY via build/test
# tooling and never ship. Each exemption is VERIFIED below against the actual
# publish output — the exemption list alone never suffices.
#   Newtonsoft.Json <- Microsoft.EntityFrameworkCore.Design (PrivateAssets=all)
#                   <- Microsoft.NET.Test.Sdk test host
$publishVerifiedExemptions = @('Newtonsoft.Json')

$raw = dotnet list GymOS.Backend.slnx package --include-transitive --format json
if ($LASTEXITCODE -ne 0) { throw 'dotnet list package failed - restore the solution first.' }

$report = ($raw -join "`n") | ConvertFrom-Json
$violations = @()

foreach ($project in $report.projects) {
    foreach ($framework in $project.frameworks) {
        $packages = @()
        if ($framework.topLevelPackages) { $packages += $framework.topLevelPackages }
        if ($framework.transitivePackages) { $packages += $framework.transitivePackages }

        foreach ($package in $packages) {
            foreach ($prefix in $bannedPrefixes) {
                $isExempt = $publishVerifiedExemptions | Where-Object { $package.id -like "$_*" }
                if ($package.id -like "$prefix*" -and -not $isExempt) {
                    $violations += "$($project.path): $($package.id) $($package.resolvedVersion)"
                }
            }
        }
    }
}

if ($violations.Count -gt 0) {
    Write-Host 'BANNED PACKAGES FOUND IN THE RESOLVED DEPENDENCY GRAPH:' -ForegroundColor Red
    $violations | Sort-Object -Unique | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    Write-Host 'See docs/Engineering/DEPENDENCY_POLICY.md (banned table).' -ForegroundColor Red
    exit 1
}

# Verify the exemptions: publish the host and assert no exempted assembly ships.
Write-Host 'Verifying tooling-only exemptions against the publish output...'
$publishDir = Join-Path ([System.IO.Path]::GetTempPath()) "gymos-audit-publish-$([guid]::NewGuid().ToString('N'))"
dotnet publish src/Host/GymOS.WebApi/GymOS.WebApi.csproj --configuration Release --output $publishDir --nologo -v quiet | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed during exemption verification.' }

$shipped = @()
foreach ($exempt in $publishVerifiedExemptions) {
    $hit = Get-ChildItem -Path $publishDir -Filter "$exempt*.dll" -Recurse -ErrorAction SilentlyContinue
    if ($hit) { $shipped += $hit.Name }
}
Remove-Item -Recurse -Force $publishDir

if ($shipped.Count -gt 0) {
    Write-Host "EXEMPTED PACKAGES ACTUALLY SHIP IN THE PUBLISH OUTPUT: $($shipped -join ', ')" -ForegroundColor Red
    Write-Host 'The tooling-only assumption no longer holds - remove the exemption or the dependency.' -ForegroundColor Red
    exit 1
}

Write-Host 'Transitive dependency audit passed - no banned packages in the resolved graph or publish output.'
