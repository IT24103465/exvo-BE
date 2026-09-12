#requires -Version 5.1
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$secretFile = Join-Path $PSScriptRoot '.env.local'
if (-not (Test-Path -LiteralPath $secretFile -PathType Leaf)) {
    throw 'Missing .env.local. Copy .env.example to .env.local and set AZURE_MYSQL_PASSWORD.'
}

# Parse data only: never execute or expand the contents of the secret file.
# Everything after the first = is literal, including #, $, backticks and semicolons.
$password = $null
try {
    $lines = [System.IO.File]::ReadAllLines($secretFile)
} catch {
    throw 'Unable to read .env.local. Check file permissions.'
}
foreach ($line in $lines) {
    if ($line -match '^\s*AZURE_MYSQL_PASSWORD\s*=(.*)$') {
        if ($null -ne $password) { throw 'Duplicate AZURE_MYSQL_PASSWORD entries in .env.local.' }
        $password = $Matches[1].Trim()
        if ($password.Length -ge 2 -and
            (($password.StartsWith('"') -and $password.EndsWith('"')) -or
             ($password.StartsWith("'") -and $password.EndsWith("'")))) {
            $password = $password.Substring(1, $password.Length - 2)
        }
    }
}
if ([string]::IsNullOrWhiteSpace($password) -or $password -eq 'YOUR_AZURE_MYSQL_PASSWORD' -or $password -eq '<password>') {
    throw 'Set AZURE_MYSQL_PASSWORD to a non-empty password in .env.local; replace the example placeholder.'
}

$projects = @(
    @{ Name = 'AuthService'; Path = 'src/services/AuthService/ExvoAuthService.csproj' },
    @{ Name = 'CatalogService'; Path = 'src/services/CatalogService/Exvo.CatalogService.csproj' },
    @{ Name = 'API Gateway'; Path = 'src/gateway/ApiGateway/ApiGateway.csproj' }
)
foreach ($project in $projects) {
    $project.Path = Join-Path $PSScriptRoot $project.Path
    if (-not (Test-Path -LiteralPath $project.Path -PathType Leaf)) {
        throw "Required project file not found: $($project.Path)"
    }
}
if (-not (Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue)) {
    throw 'The .NET 8 SDK is required. Install it and ensure dotnet is on PATH.'
}
$shell = Join-Path $PSHOME 'pwsh.exe'
if (-not (Test-Path -LiteralPath $shell)) { $shell = Join-Path $PSHOME 'powershell.exe' }
if (-not (Test-Path -LiteralPath $shell)) { throw 'This launcher requires PowerShell on Windows.' }

Add-Type -AssemblyName System.Data
$variableNames = @('EXVO_AUTH_MYSQL_CONNECTION_STRING', 'EXVO_CATALOG_MYSQL_CONNECTION_STRING')
$databases = @('exvo_auth_db', 'exvo_catalog_db')
$previous = @{}
foreach ($name in $variableNames) { $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
    for ($i = 0; $i -lt $variableNames.Count; $i++) {
        # The builder safely quotes passwords containing connection-string delimiters.
        $connection = New-Object System.Data.Common.DbConnectionStringBuilder
        $connection['Server'] = 'exvo-db.mysql.database.azure.com'
        $connection['Port'] = '3306'
        $connection['Database'] = $databases[$i]
        $connection['User ID'] = 'exvoadmin'
        $connection['Password'] = $password
        $connection['SslMode'] = 'Required'
        [Environment]::SetEnvironmentVariable($variableNames[$i], $connection.get_ConnectionString() + ';', 'Process')
    }
    foreach ($project in $projects) {
        # Only public paths appear in process arguments. Secrets are inherited via environment.
        $quotedPath = $project.Path.Replace("'", "''")
        $command = "& dotnet run --project '$quotedPath' --launch-profile http"
        $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
        try {
            Start-Process -FilePath $shell -WorkingDirectory $PSScriptRoot -WindowStyle Normal -ArgumentList @('-NoLogo', '-NoProfile', '-NoExit', '-EncodedCommand', $encoded) | Out-Null
        } catch {
            throw "Unable to launch $($project.Name). Close any windows already launched before retrying."
        }
        Write-Host "Launched $($project.Name) in its own PowerShell window (http profile)."
    }
} finally {
    # Restore the caller's environment; each service window retains its inherited copy.
    foreach ($name in $variableNames) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
    $password = $null
    $lines = $null
    $line = $null
    $Matches = $null
    $connection = $null
}
