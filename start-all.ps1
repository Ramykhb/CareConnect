# Starts everything needed for local development with one command:
#   .\start-all.ps1
# Mailpit (confirmation emails) and RabbitMQ run in Docker; both apps run inside this same window
# (no extra windows open). Press Ctrl+C here to stop both apps.

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

# If an earlier copy of the apps is still running it holds ports 5101/5102 and the new copy would crash with
# "address already in use". Stop that old copy first; refuse if the port belongs to some other program.
foreach ($port in 5101, 5102) {
    $owners = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue | Select-Object -ExpandProperty OwningProcess -Unique
    foreach ($id in $owners) {
        $process = Get-Process -Id $id -ErrorAction SilentlyContinue
        if ($null -eq $process) { continue }
        if ($process.ProcessName -in "PetSitting", "BabySitting", "dotnet") {
            Write-Host "Port $port is used by an earlier copy of the app ($($process.ProcessName), id $id) - stopping it."
            Stop-Process -Id $id -Force
        } else {
            Write-Error "Port $port is used by another program ($($process.ProcessName), id $id). Close it, then run this script again."
            exit 1
        }
    }
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

# Both apps run in this window (no new windows); their logs appear here together.
$apps = foreach ($app in "PetSitting", "BabySitting") {
    Start-Process dotnet -WorkingDirectory $root -ArgumentList "run", "--project", "src/$app", "--no-build" -NoNewWindow -PassThru
}

Write-Host ""
Write-Host "PetSitting:  http://localhost:5101"
Write-Host "BabySitting: http://localhost:5102"
Write-Host "Mailpit:     http://localhost:8025"
Write-Host "RabbitMQ:    http://localhost:15672"
Write-Host "Press Ctrl+C to stop both apps."
Write-Host ""

# Keep this window attached to the apps; when it stops (Ctrl+C or closing it), stop both apps too.
# "dotnet run" starts the real app as a child process, so stop each whole process tree (/T), not just dotnet.
try { $apps | Wait-Process }
finally { foreach ($app in $apps) { taskkill /PID $app.Id /T /F *> $null } }
