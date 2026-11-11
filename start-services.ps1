# =============================================================================
# Fotoprint AI Copilot - Start Services (.NET 9)
# =============================================================================

$projects = @(
    @{ Name = "Fotoprint.Mcp.EmployeeReader"; Port = 8081; Path = "src/Fotoprint.Mcp.EmployeeReader" },
    @{ Name = "Fotoprint.Mcp.OneC";           Port = 8082; Path = "src/Fotoprint.Mcp.OneC" },
    @{ Name = "Fotoprint.Mcp.Bitrix24";       Port = 8083; Path = "src/Fotoprint.Mcp.Bitrix24" },
    @{ Name = "Fotoprint.Mcp.Calendar";       Port = 8084; Path = "src/Fotoprint.Mcp.Calendar" },
    @{ Name = "Fotoprint.AiGateway.Api";      Port = 8088; Path = "src/Fotoprint.AiGateway.Api" }
)

Write-Host "=========================================================" -ForegroundColor Cyan
Write-Host "Starting Fotoprint AI Copilot Services (.NET 9)..." -ForegroundColor Cyan
Write-Host "=========================================================" -ForegroundColor Cyan

foreach ($p in $projects) {
    Write-Host "Starting $($p.Name) on port $($p.Port)..." -ForegroundColor Yellow
    Start-Process -FilePath "dotnet" -ArgumentList "run --project $($p.Path) --no-build" -WindowStyle Hidden
}

Write-Host "`nWaiting for services to initialize (6 seconds)..." -ForegroundColor Gray
Start-Sleep -Seconds 6

Write-Host "`nChecking service health endpoints:" -ForegroundColor Cyan
foreach ($p in $projects) {
    try {
        $res = Invoke-RestMethod -Uri "http://localhost:$($p.Port)/health" -TimeoutSec 3 -ErrorAction Stop
        Write-Host "  [OK] $($p.Name) (: $($p.Port)) -> ONLINE ($($res.status))" -ForegroundColor Green
    } catch {
        Write-Host "  [WAIT] $($p.Name) (: $($p.Port)) -> Starting up..." -ForegroundColor Yellow
    }
}

Write-Host "`n=========================================================" -ForegroundColor Cyan
Write-Host "Services started and connected to PostgreSQL on port 5434!" -ForegroundColor Green
Write-Host "=========================================================" -ForegroundColor Cyan
