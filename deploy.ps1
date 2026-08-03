# Deploy SpaceRadarBot to the production VM (GCP free tier).
# Usage: .\deploy.ps1            - test, publish, upload, restart
#        .\deploy.ps1 -SkipTests - skip the test run
#
# The script never touches appsettings.json or spaceradar.db on the server:
# config and database live only there and survive every deploy.

param(
    [string]$VmHost = "",                # taken from deploy.local.json if not passed
    [string]$VmUser = "spaceradar",
    [string]$KeyPath = "$env:USERPROFILE\.ssh\id_ed25519",
    [string]$BotDir = "/home/spaceradar/bot",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$repoRoot = $PSScriptRoot
$publishDir = Join-Path $repoRoot "publish"

# The VM address stays out of version control: deploy.local.json (gitignored).
if (-not $VmHost) {
    $localCfg = Join-Path $repoRoot "deploy.local.json"
    if (Test-Path $localCfg) {
        $VmHost = (Get-Content $localCfg -Raw | ConvertFrom-Json).VmHost
    }
}
if (-not $VmHost) {
    throw "VmHost is not set. Pass -VmHost <ip> or create deploy.local.json (copy deploy.local.example.json; current IP is in the GCP console)."
}

$sshTarget = "$VmUser@$VmHost"

function Invoke-Step($name, $script) {
    Write-Host "==> $name" -ForegroundColor Cyan
    & $script
    if ($LASTEXITCODE -ne 0) { throw "Step failed: $name (exit $LASTEXITCODE)" }
}

if (-not $SkipTests) {
    Invoke-Step "Tests" { dotnet test (Join-Path $repoRoot "SpaceRadarBot.Tests\SpaceRadarBot.Tests.csproj") --nologo -v q }
}

Invoke-Step "Publish" {
    if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
    dotnet publish (Join-Path $repoRoot "SpaceRadarBot\SpaceRadarBot.csproj") -c Release -o $publishDir --nologo
}

# Never ship local config: the server keeps its own appsettings.json.
Remove-Item -Force -ErrorAction SilentlyContinue (Join-Path $publishDir "appsettings.json")

Invoke-Step "Upload" {
    ssh -i $KeyPath $sshTarget "rm -rf /tmp/spaceradar-deploy && mkdir -p /tmp/spaceradar-deploy"
    scp -i $KeyPath -r "$publishDir\*" "${sshTarget}:/tmp/spaceradar-deploy/"
}

Invoke-Step "Swap + restart" {
    # Stop, wipe old binaries (keeps config + db), move new ones in, start.
    ssh -i $KeyPath $sshTarget ("sudo systemctl stop telegrambot && " +
        "find $BotDir -maxdepth 1 -type f ! -name 'appsettings.json' ! -name 'spaceradar.db' -delete && " +
        "mv /tmp/spaceradar-deploy/* $BotDir/ && rmdir /tmp/spaceradar-deploy && " +
        "sudo systemctl start telegrambot")
}

Write-Host "==> Waiting for startup..." -ForegroundColor Cyan
Start-Sleep -Seconds 10
ssh -i $KeyPath $sshTarget "systemctl is-active telegrambot && sudo journalctl -u telegrambot -n 15 --no-pager --since '-1 min'"
if ($LASTEXITCODE -ne 0) { throw "Service is not active after deploy - check logs: ssh $sshTarget 'sudo journalctl -u telegrambot -n 50'" }

Write-Host "==> Deploy OK" -ForegroundColor Green
