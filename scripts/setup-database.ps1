<#
.SYNOPSIS
    Creates or updates the FaceAttendance database on a local SQL Server instance.

.DESCRIPTION
    Applies the EF Core migrations. Safe to run repeatedly: migrations already applied are
    skipped.

.EXAMPLE
    .\setup-database.ps1
    .\setup-database.ps1 -Server ".\SQLEXPRESS" -Database "FaceAttendance"
#>
[CmdletBinding()]
param(
    [string]$Server = '.\SQLEXPRESS',
    [string]$Database = 'FaceAttendance'
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')

$connectionString = "Server=$Server;Database=$Database;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True"
Write-Host "Applying migrations to $Server / $Database" -ForegroundColor Cyan

Push-Location $root
try {
    if (-not (Test-Path (Join-Path $root 'dotnet-tools.json')) -and
        -not (Test-Path (Join-Path $root '.config\dotnet-tools.json'))) {
        dotnet new tool-manifest --force | Out-Null
    }

    dotnet tool restore | Out-Null
    if ($LASTEXITCODE -ne 0) { dotnet tool install dotnet-ef | Out-Null }

    dotnet ef database update `
        --project src/Infra/FaceAttendance.Infrastructure.csproj `
        --startup-project src/Api/FaceAttendance.Api.csproj `
        --connection $connectionString

    if ($LASTEXITCODE -ne 0) { throw 'Migration failed.' }

    Write-Host 'Database is up to date.' -ForegroundColor Green
    Write-Host ''
    Write-Host 'If this is not the connection string your app uses, update' -ForegroundColor Yellow
    Write-Host 'ConnectionStrings:Default in src/Api/appsettings.json to match.' -ForegroundColor Yellow
}
finally {
    Pop-Location
}
