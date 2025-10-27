# Smart bot addition script - fills server to specified player count
# Usage: .\add-bots.ps1 [-Target 24]

param(
    [int]$Target = 20  # Default to 20 bots (server maximum)
)

$ApiUrl = "http://localhost:5100"

Write-Host "Checking current player count..." -ForegroundColor Cyan

try {
    # Get current player list
    $response = Invoke-RestMethod -Uri "$ApiUrl/api/server/players" -Method Get

    $current = $response.totalPlayers
    Write-Host "Current players: $current" -ForegroundColor Green
    Write-Host "Target players: $Target" -ForegroundColor Green

    $botsNeeded = $Target - $current

    if ($botsNeeded -le 0) {
        Write-Host "Server already has $current players (target: $Target). No bots needed!" -ForegroundColor Yellow
        exit 0
    }

    Write-Host "Adding $botsNeeded bots..." -ForegroundColor Cyan

    for ($i = 1; $i -le $botsNeeded; $i++) {
        Write-Host "Adding bot $i/$botsNeeded... " -NoNewline

        try {
            $result = Invoke-RestMethod -Uri "$ApiUrl/api/server/command" -Method Post `
                -ContentType "application/json" `
                -Body (@{command = "/bot"} | ConvertTo-Json)

            if ($result.message -match "success") {
                Write-Host "✓" -ForegroundColor Green
            } else {
                Write-Host "✗ $($result.message)" -ForegroundColor Red
            }
        }
        catch {
            Write-Host "✗ Failed: $($_.Exception.Message)" -ForegroundColor Red
        }

        Start-Sleep -Milliseconds 500  # Wait 500ms between bots
    }

    Write-Host ""
    Write-Host "Done! Checking final count..." -ForegroundColor Cyan
    Start-Sleep -Seconds 1

    # Verify final count
    $finalResponse = Invoke-RestMethod -Uri "$ApiUrl/api/server/players" -Method Get
    $finalCount = $finalResponse.totalPlayers

    Write-Host "Final player count: $finalCount" -ForegroundColor Green

    if ($finalCount -ge $Target) {
        Write-Host "✓ Target reached!" -ForegroundColor Green
    } else {
        Write-Host "⚠ Target not fully reached ($finalCount/$Target)" -ForegroundColor Yellow
    }
}
catch {
    Write-Host "Error: Could not connect to API. Is the server running?" -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}
