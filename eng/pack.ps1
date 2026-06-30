#!/usr/bin/env pwsh
# Builds and packs all shipping VeloORM packages into ./artifacts.
param([string]$Configuration = "Release")

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

dotnet build VeloORM.slnx -c $Configuration
dotnet pack VeloORM.slnx -c $Configuration --no-build -o artifacts

Write-Host "`nPackages produced:" -ForegroundColor Cyan
Get-ChildItem artifacts -Filter *.nupkg | ForEach-Object { Write-Host "  $($_.Name)" }
