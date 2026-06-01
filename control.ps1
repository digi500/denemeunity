param (
    [string]$action = "status"
)

$port = 52424
$baseUrl = "http://127.0.0.1:$port"

try {
    switch ($action) {
        "status" {
            $res = Invoke-RestMethod -Uri "$baseUrl/status" -Method Get
            $res | ConvertTo-Json
        }
        "play" {
            $res = Invoke-RestMethod -Uri "$baseUrl/play" -Method Post
            $res | ConvertTo-Json
        }
        "stop" {
            $res = Invoke-RestMethod -Uri "$baseUrl/stop" -Method Post
            $res | ConvertTo-Json
        }
        "hierarchy" {
            $res = Invoke-RestMethod -Uri "$baseUrl/hierarchy" -Method Get
            $res | ConvertTo-Json -Depth 10
        }
        "logs" {
            $res = Invoke-RestMethod -Uri "$baseUrl/logs" -Method Get
            $res | ConvertTo-Json -Depth 5
        }
        "screenshot" {
            $outFile = Join-Path $PSScriptRoot "screenshot.png"
            Write-Host "Requesting screenshot..."
            Invoke-WebRequest -Uri "$baseUrl/screenshot" -OutFile $outFile -ErrorAction Stop
            Write-Host "Screenshot saved to: $outFile"
        }
        "buildscene" {
            $res = Invoke-RestMethod -Uri "$baseUrl/buildscene" -Method Post
            $res | ConvertTo-Json
        }
        "setupgameplay" {
            $res = Invoke-RestMethod -Uri "$baseUrl/setupgameplay" -Method Post
            $res | ConvertTo-Json
        }
        "buildwebgl" {
            $res = Invoke-RestMethod -Uri "$baseUrl/buildwebgl" -Method Post
            $res | ConvertTo-Json
        }
        default {
            Write-Error "Unknown action: $action. Supported actions: status, play, stop, hierarchy, logs, screenshot, buildscene, setupgameplay, buildwebgl"
        }
    }
} catch {
    Write-Error "Failed to connect to Agent Integration Server at $baseUrl. Make sure Unity is running and the project is open.`nDetails: $_"
}
