param(
    [Parameter(Mandatory = $true)]
    [string]$Endpoint
)

$headers = @{
    "Accept" = "application/json, text/event-stream"
    "MCP-Protocol-Version" = "2025-11-25"
}

function Invoke-Mcp([string]$Body) {
    try {
        $response = Invoke-WebRequest -Uri $Endpoint -Method Post -ContentType "application/json" -Headers $headers -Body $Body
        Write-Host ("HTTP " + [int]$response.StatusCode)
        if ([string]::IsNullOrWhiteSpace($response.Content)) {
            return $null
        }
        $response.Content | ConvertFrom-Json
    }
    catch {
        Write-Host ("REQUEST FAILED: " + $_.Exception.Message) -ForegroundColor Red
        if ($_.ErrorDetails -and $_.ErrorDetails.Message) {
            Write-Host $_.ErrorDetails.Message -ForegroundColor Yellow
        }
        elseif ($_.Exception.Response) {
            try {
                $reader = New-Object System.IO.StreamReader($_.Exception.Response.GetResponseStream())
                $body = $reader.ReadToEnd()
                if (-not [string]::IsNullOrWhiteSpace($body)) { Write-Host $body -ForegroundColor Yellow }
            } catch { }
        }
        throw
    }
}

$init = '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"powershell-test","version":"1.0"}}}'
$initialized = '{"jsonrpc":"2.0","method":"notifications/initialized","params":{}}'
$tools = '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}'
$status = '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"unity_get_status","arguments":{}}}'

"INITIALIZE"
Invoke-Mcp $init | ConvertTo-Json -Depth 20
"INITIALIZED NOTIFICATION"
Invoke-Mcp $initialized | ConvertTo-Json -Depth 20
"TOOLS"
Invoke-Mcp $tools | ConvertTo-Json -Depth 20
"STATUS"
Invoke-Mcp $status | ConvertTo-Json -Depth 20
