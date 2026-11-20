# Starts everything needed for local development with one command:
#   .\start-all.ps1
# Mailpit (confirmation emails) runs in Docker; each app opens in its own window.
# Close an app window (or press Ctrl+C in it) to stop that app.

$root = $PSScriptRoot

# Mailpit needs Docker Desktop. Start it if needed and wait until it answers.
docker info *> $null
if ($LASTEXITCODE -ne 0) {
    Write-Host "Starting Docker Desktop..."
    Start-Process "C:\Program Files\Docker\Docker\Docker Desktop.exe"
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep -Seconds 4
        docker info *> $null
        if ($LASTEXITCODE -eq 0) { break }
    }
}
docker compose -f (Join-Path $root "docker-compose.auth.yml") up -d

# RabbitMQ (cross-app messaging and combined care) needs a .env file with its login.
if (Test-Path (Join-Path $root ".env")) {
    docker compose -f (Join-Path $root "docker-compose.yml") up -d rabbitmq
} else {
    Write-Warning "RabbitMQ not started: copy .env.example to .env and set RABBITMQ_PASSWORD (see README, 'Local setup')."
}

# Build once first: two apps building at the same time fight over the shared Messaging project.
foreach ($app in "PetSitting", "BabySitting") {
    dotnet build (Join-Path $root "src/$app") -v q --nologo
    if ($LASTEXITCODE -ne 0) { Write-Error "$app build failed - fix the errors above, then run this script again."; exit 1 }
}

# ASP.NET does not read .env by itself. Copy its KEY=value lines (for example GROQ_API_KEY and Ai__Enabled)
# into this process's environment, so both app windows started below inherit them. Values are never printed.
$envFile = Join-Path $root ".env"
if (Test-Path $envFile) {
    foreach ($line in Get-Content $envFile) {
        if ($line -match '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*?)\s*$') {
            $value = $Matches[2]
            if ($value.Length -ge 2 -and $value[0] -eq $value[-1] -and $value[0] -in '"', "'") { $value = $value.Substring(1, $value.Length - 2) }
            [Environment]::SetEnvironmentVariable($Matches[1], $value, "Process")
        }
    }
}

# One window per app.
foreach ($app in "PetSitting", "BabySitting") {
    Start-Process powershell -WorkingDirectory $root -ArgumentList "-NoExit", "-Command", "`$host.UI.RawUI.WindowTitle = '$app'; dotnet run --project src/$app --no-build"
}

Write-Host ""
Write-Host "PetSitting:  http://localhost:5101"
Write-Host "BabySitting: http://localhost:5102"
Write-Host "Mailpit:     http://localhost:8025"
Write-Host "RabbitMQ:    http://localhost:15672"
