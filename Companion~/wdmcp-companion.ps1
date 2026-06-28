param(
    [Parameter(Mandatory = $true)]
    [string]$ConfigPath
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$Utf8NoBom = [System.Text.UTF8Encoding]::new($false)
$Script:Listener = $null
$Script:NgrokProcess = $null
$Script:OwnsNgrok = $false
$Script:StartedUtc = [DateTime]::UtcNow.ToString('O')
$Script:LastRequestUtc = ''
$Script:LastError = ''
$Script:PublicUrl = ''
$Script:NgrokState = 'Stopped'

function Read-JsonFile([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    $raw = [System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8)
    if ([string]::IsNullOrWhiteSpace($raw)) { return $null }
    return $raw | ConvertFrom-Json
}

function Write-TextAtomic([string]$Path, [string]$Text) {
    $directory = [System.IO.Path]::GetDirectoryName($Path)
    if (-not [string]::IsNullOrWhiteSpace($directory)) { [System.IO.Directory]::CreateDirectory($directory) | Out-Null }
    $temporary = $Path + '.tmp-' + [Guid]::NewGuid().ToString('N')
    [System.IO.File]::WriteAllText($temporary, $Text, $Utf8NoBom)
    if (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue }
    Move-Item -LiteralPath $temporary -Destination $Path -Force
}

function Write-JsonAtomic([string]$Path, $Value) {
    Write-TextAtomic $Path ($Value | ConvertTo-Json -Depth 100 -Compress)
}

function Add-Log([string]$Message) {
    try {
        $line = ([DateTime]::UtcNow.ToString('O') + ' ' + $Message + [Environment]::NewLine)
        [System.IO.File]::AppendAllText([string]$Config.companionLogPath, $line, $Utf8NoBom)
    } catch { }
}

function Test-ProcessAlive([int]$ProcessId) {
    if ($ProcessId -le 0) { return $false }
    try { return $null -ne (Get-Process -Id $ProcessId -ErrorAction Stop) } catch { return $false }
}

function Get-UnityAvailability {
    try {
        $status = Read-JsonFile ([string]$Config.unityStatusPath)
        if ($null -eq $status -or [string]::IsNullOrWhiteSpace([string]$status.timestampUtc)) { return $false }
        $time = [DateTime]::Parse([string]$status.timestampUtc).ToUniversalTime()
        return (([DateTime]::UtcNow - $time).TotalSeconds -le 5.0)
    } catch { return $false }
}

function Write-Status([string]$State) {
    $ngrokPid = 0
    if ($null -ne $Script:NgrokProcess) { try { if (-not $Script:NgrokProcess.HasExited) { $ngrokPid = $Script:NgrokProcess.Id } } catch { } }
    $mcp = ''
    if (-not [string]::IsNullOrWhiteSpace($Script:PublicUrl)) {
        $mcp = $Script:PublicUrl.TrimEnd('/') + '/' + [string]$Config.capabilityToken + '/mcp'
    }
    $status = [ordered]@{
        companionVersion = [string]$Config.companionVersion
        state = $State
        processId = $PID
        localEndpoint = ('http://127.0.0.1:' + [string]$Config.port + '/' + [string]$Config.capabilityToken + '/mcp')
        publicUrl = $Script:PublicUrl
        mcpEndpoint = $mcp
        ngrokState = $Script:NgrokState
        ngrokProcessId = $ngrokPid
        ownsNgrok = $Script:OwnsNgrok
        unityAvailable = (Get-UnityAvailability)
        lastError = $Script:LastError
        lastRequestUtc = $Script:LastRequestUtc
        startedUtc = $Script:StartedUtc
        timestampUtc = [DateTime]::UtcNow.ToString('O')
    }
    Write-JsonAtomic ([string]$Config.companionStatusPath) $status
}

function Get-NgrokPublicUrl {
    try {
        $response = Invoke-RestMethod -Uri 'http://127.0.0.1:4040/api/tunnels' -Method Get -TimeoutSec 2
        foreach ($tunnel in @($response.tunnels)) {
            $url = [string]$tunnel.public_url
            if ($url.StartsWith('https://', [StringComparison]::OrdinalIgnoreCase)) { return $url.TrimEnd('/') }
        }
    } catch { }
    return ''
}

function Start-NgrokIfConfigured {
    if (-not [bool]$Config.autoStartNgrok) {
        $Script:NgrokState = 'Disabled'
        return
    }

    $existing = Get-NgrokPublicUrl
    if (-not [string]::IsNullOrWhiteSpace($existing)) {
        $Script:PublicUrl = $existing
        $Script:NgrokState = 'Ready (external)'
        return
    }

    $Script:NgrokState = 'Starting'
    Write-Status 'Starting'
    $ngrokArguments = 'http '
    if (-not [string]::IsNullOrWhiteSpace([string]$Config.stableNgrokUrl)) {
        $safeStableUrl = ([string]$Config.stableNgrokUrl).Replace('"', '')
        $ngrokArguments += '--url="' + $safeStableUrl + '" '
    }
    $safeLogPath = ([string]$Config.ngrokLogPath).Replace('"', '')
    $ngrokArguments += [string]$Config.port + ' --log="' + $safeLogPath + '" --log-format=json'
    try {
        $ngrokStartInfo = [System.Diagnostics.ProcessStartInfo]::new()
        $ngrokStartInfo.FileName = [string]$Config.ngrokExecutablePath
        $ngrokStartInfo.Arguments = $ngrokArguments
        $ngrokStartInfo.UseShellExecute = $false
        $ngrokStartInfo.CreateNoWindow = $true
        $Script:NgrokProcess = [System.Diagnostics.Process]::Start($ngrokStartInfo)
        if ($null -eq $Script:NgrokProcess) { throw 'ngrok process did not start.' }
        $Script:OwnsNgrok = $true
    } catch {
        $Script:NgrokState = 'Error'
        $Script:LastError = 'Unable to start ngrok: ' + $_.Exception.Message
        Add-Log $Script:LastError
        return
    }

    $deadline = [DateTime]::UtcNow.AddSeconds(90)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath ([string]$Config.stopFlagPath)) { return }
        try { if ($Script:NgrokProcess.HasExited) { break } } catch { break }
        $url = Get-NgrokPublicUrl
        if (-not [string]::IsNullOrWhiteSpace($url)) {
            $Script:PublicUrl = $url
            $Script:NgrokState = 'Ready'
            return
        }
        Start-Sleep -Milliseconds 250
    }

    $Script:NgrokState = 'Error'
    if ([string]::IsNullOrWhiteSpace($Script:LastError)) {
        $Script:LastError = 'Timed out while waiting for ngrok public URL. Check companion and ngrok logs.'
    }
    Add-Log $Script:LastError
}

function Get-HeaderEnd([byte[]]$Bytes) {
    for ($i = 0; $i -le $Bytes.Length - 4; $i++) {
        if ($Bytes[$i] -eq 13 -and $Bytes[$i+1] -eq 10 -and $Bytes[$i+2] -eq 13 -and $Bytes[$i+3] -eq 10) { return $i }
    }
    return -1
}

function Read-HttpRequest([System.Net.Sockets.TcpClient]$Client) {
    $stream = $Client.GetStream()
    $stream.ReadTimeout = 15000
    $memory = [System.IO.MemoryStream]::new()
    $buffer = [byte[]]::new(8192)
    $headerEnd = -1
    while ($headerEnd -lt 0) {
        $read = $stream.Read($buffer, 0, $buffer.Length)
        if ($read -le 0) { throw 'Client closed before sending HTTP headers.' }
        $memory.Write($buffer, 0, $read)
        if ($memory.Length -gt 32768) { throw 'HTTP headers exceeded 32 KB.' }
        $headerEnd = Get-HeaderEnd ($memory.ToArray())
    }

    $all = $memory.ToArray()
    $headerText = [System.Text.Encoding]::ASCII.GetString($all, 0, $headerEnd)
    $lines = $headerText -split "`r`n"
    $requestParts = $lines[0] -split ' '
    if ($requestParts.Length -lt 2) { throw 'Invalid HTTP request line.' }
    $method = $requestParts[0]
    $path = $requestParts[1]
    $headers = @{}
    for ($i = 1; $i -lt $lines.Length; $i++) {
        $separator = $lines[$i].IndexOf(':')
        if ($separator -gt 0) { $headers[$lines[$i].Substring(0,$separator).Trim().ToLowerInvariant()] = $lines[$i].Substring($separator+1).Trim() }
    }
    $contentLength = 0
    if ($headers.ContainsKey('content-length')) { $contentLength = [int]$headers['content-length'] }
    if ($contentLength -gt 1048576) { throw 'HTTP body exceeded 1 MB.' }
    $bodyOffset = $headerEnd + 4
    $bodyMemory = [System.IO.MemoryStream]::new()
    if ($all.Length -gt $bodyOffset) { $bodyMemory.Write($all, $bodyOffset, $all.Length - $bodyOffset) }
    while ($bodyMemory.Length -lt $contentLength) {
        $read = $stream.Read($buffer, 0, [Math]::Min($buffer.Length, $contentLength - [int]$bodyMemory.Length))
        if ($read -le 0) { throw 'Client closed before sending the complete HTTP body.' }
        $bodyMemory.Write($buffer, 0, $read)
    }
    $bodyBytes = $bodyMemory.ToArray()
    $body = [System.Text.Encoding]::UTF8.GetString($bodyBytes, 0, $contentLength)
    return [ordered]@{ Method=$method; Path=$path; Body=$body; Stream=$stream }
}

function Write-HttpResponse($Stream, [int]$StatusCode, [string]$Reason, [string]$Body, [string]$ContentType = 'application/json') {
    if ($null -eq $Body) { $Body = '' }
    $bodyBytes = [System.Text.Encoding]::UTF8.GetBytes($Body)
    $headers = "HTTP/1.1 $StatusCode $Reason`r`nContent-Type: $ContentType; charset=utf-8`r`nContent-Length: $($bodyBytes.Length)`r`nConnection: close`r`nCache-Control: no-store`r`n`r`n"
    $headerBytes = [System.Text.Encoding]::ASCII.GetBytes($headers)
    $Stream.Write($headerBytes, 0, $headerBytes.Length)
    if ($bodyBytes.Length -gt 0) { $Stream.Write($bodyBytes, 0, $bodyBytes.Length) }
    $Stream.Flush()
}

function New-RpcSuccess($Id, $Result) {
    return [ordered]@{ jsonrpc='2.0'; id=$Id; result=$Result }
}

function New-RpcError($Id, [int]$Code, [string]$Message, [string]$Data) {
    return [ordered]@{ jsonrpc='2.0'; id=$Id; error=[ordered]@{ code=$Code; message=$Message; data=$Data } }
}

function New-ToolError([string]$Message) {
    return [ordered]@{ content=@([ordered]@{type='text';text=$Message}); isError=$true }
}

function Invoke-UnityTool([string]$Name, $Arguments) {
    if (-not (Get-UnityAvailability)) {
        return New-ToolError 'Unity Editor is temporarily unavailable, compiling, or reloading. The MCP companion is still connected; retry this tool after Unity finishes.'
    }

    $requestId = [Guid]::NewGuid().ToString('N')
    $requestPath = Join-Path ([string]$Config.requestsPath) ($requestId + '.json')
    $responsePath = Join-Path ([string]$Config.responsesPath) ($requestId + '.json')
    $request = [ordered]@{
        id = $requestId
        name = $Name
        arguments = $(if ($null -eq $Arguments) { [ordered]@{} } else { $Arguments })
        createdUtc = [DateTime]::UtcNow.ToString('O')
    }
    Write-JsonAtomic $requestPath $request

    $deadline = [DateTime]::UtcNow.AddSeconds([int]$Config.requestTimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $responsePath) {
            try {
                $response = Read-JsonFile $responsePath
                Remove-Item -LiteralPath $responsePath -Force -ErrorAction SilentlyContinue
                if ([bool]$response.success) {
                    $text = $response.result | ConvertTo-Json -Depth 100
                    return [ordered]@{ content=@([ordered]@{type='text';text=$text}); isError=$false }
                }
                return New-ToolError ('Unity tool failed: ' + [string]$response.error)
            } catch {
                return New-ToolError ('Unable to read Unity IPC response: ' + $_.Exception.Message)
            }
        }
        if (-not (Get-UnityAvailability)) {
            Start-Sleep -Milliseconds 250
        } else {
            Start-Sleep -Milliseconds 75
        }
    }
    return New-ToolError ('Unity tool timed out after ' + [string]$Config.requestTimeoutSeconds + ' seconds. Unity may still be compiling or processing a large folder.')
}

function Handle-Rpc($Rpc) {
    $method = [string]$Rpc.method
    $idProperty = $Rpc.PSObject.Properties['id']
    $notification = ($null -eq $idProperty)
    $id = if ($notification) { $null } else { $Rpc.id }

    switch ($method) {
        'initialize' {
            if ($notification) { return $null }
            $requested = [string]$Rpc.params.protocolVersion
            $selected = if (@('2025-11-25','2025-06-18','2025-03-26') -contains $requested) { $requested } else { '2025-11-25' }
            return New-RpcSuccess $id ([ordered]@{
                protocolVersion=$selected
                capabilities=[ordered]@{tools=[ordered]@{listChanged=$true}}
                serverInfo=[ordered]@{name='wasim-unity-mcp-bridge';title='Wasim Unity MCP Bridge';version=[string]$Config.companionVersion}
                instructions='The standalone companion keeps MCP and ngrok alive across Unity script reloads. Unity project tools are executed through a local queue. During compilation a tool may report Unity temporarily unavailable; retry it without recreating the app.'
            })
        }
        'notifications/initialized' { return $null }
        'notifications/cancelled' { return $null }
        'ping' { if ($notification) { return $null }; return New-RpcSuccess $id ([ordered]@{}) }
        'tools/list' {
            if ($notification) { return $null }
            $catalog = Read-JsonFile ([string]$Config.catalogPath)
            if ($null -eq $catalog) { return New-RpcError $id -32603 'Tool catalogue unavailable' 'Unity has not exported tool-catalog.json yet.' }
            return New-RpcSuccess $id ([ordered]@{tools=@($catalog)})
        }
        'tools/call' {
            if ($notification) { return $null }
            $name = [string]$Rpc.params.name
            if ([string]::IsNullOrWhiteSpace($name)) { return New-RpcError $id -32602 'Invalid params' 'tools/call requires a tool name.' }
            $result = Invoke-UnityTool $name $Rpc.params.arguments
            return New-RpcSuccess $id $result
        }
        default {
            if ($notification) { return $null }
            return New-RpcError $id -32601 'Method not found' $method
        }
    }
}

try {
    if (-not (Test-Path -LiteralPath $ConfigPath)) { throw "Companion config not found: $ConfigPath" }
    $Script:Config = Read-JsonFile $ConfigPath
    if ($null -eq $Config) { throw 'Companion config is empty or invalid.' }
    [System.IO.Directory]::CreateDirectory([string]$Config.ipcRoot) | Out-Null
    [System.IO.Directory]::CreateDirectory([string]$Config.requestsPath) | Out-Null
    [System.IO.Directory]::CreateDirectory([string]$Config.responsesPath) | Out-Null
    if (Test-Path -LiteralPath ([string]$Config.stopFlagPath)) { Remove-Item -LiteralPath ([string]$Config.stopFlagPath) -Force -ErrorAction SilentlyContinue }

    Add-Log ('Starting companion v' + [string]$Config.companionVersion + ' on port ' + [string]$Config.port)
    $Script:Listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, [int]$Config.port)
    $Script:Listener.Server.ExclusiveAddressUse = $true
    $Script:Listener.Start(20)
    $Script:LastError = ''
    Write-Status 'Running'
    Start-NgrokIfConfigured
    Write-Status 'Running'

    $lastStatusWrite = [DateTime]::MinValue
    while (-not (Test-Path -LiteralPath ([string]$Config.stopFlagPath))) {
        if (([DateTime]::UtcNow - $lastStatusWrite).TotalSeconds -ge 1) {
            $lastStatusWrite = [DateTime]::UtcNow
            $detected = Get-NgrokPublicUrl
            if (-not [string]::IsNullOrWhiteSpace($detected)) {
                $Script:PublicUrl = $detected
                if ($Script:NgrokState -ne 'Ready (external)') { $Script:NgrokState = 'Ready' }
            }
            Write-Status 'Running'
        }

        $acceptTask = $Script:Listener.AcceptTcpClientAsync()
        while (-not $acceptTask.Wait(250)) {
            if (Test-Path -LiteralPath ([string]$Config.stopFlagPath)) { break }
            if (([DateTime]::UtcNow - $lastStatusWrite).TotalSeconds -ge 1) {
                $lastStatusWrite = [DateTime]::UtcNow
                Write-Status 'Running'
            }
        }
        if (Test-Path -LiteralPath ([string]$Config.stopFlagPath)) { break }

        $client = $null
        try {
            $client = $acceptTask.Result
            $request = Read-HttpRequest $client
            $expectedPath = '/' + [string]$Config.capabilityToken + '/mcp'
            if ($request.Path -ne $expectedPath) {
                Write-HttpResponse $request.Stream 404 'Not Found' '{"error":"Not found"}'
                continue
            }
            if ($request.Method -ne 'POST') {
                Write-HttpResponse $request.Stream 405 'Method Not Allowed' '{"error":"POST required"}'
                continue
            }

            $Script:LastRequestUtc = [DateTime]::UtcNow.ToString('O')
            $rpc = $request.Body | ConvertFrom-Json
            $rpcResponse = Handle-Rpc $rpc
            if ($null -eq $rpcResponse) {
                Write-HttpResponse $request.Stream 202 'Accepted' ''
            } else {
                Write-HttpResponse $request.Stream 200 'OK' ($rpcResponse | ConvertTo-Json -Depth 100 -Compress)
            }
        } catch {
            $Script:LastError = $_.Exception.Message
            Add-Log ('Request error: ' + $Script:LastError)
            try {
                if ($null -ne $client) { Write-HttpResponse ($client.GetStream()) 500 'Internal Server Error' '{"error":"Internal server error"}' }
            } catch { }
        } finally {
            try { if ($null -ne $client) { $client.Close() } } catch { }
            Write-Status 'Running'
        }
    }
} catch {
    $Script:LastError = $_.Exception.Message
    Add-Log ('Fatal error: ' + $Script:LastError)
    try { Write-Status 'Error' } catch { }
} finally {
    try { if ($null -ne $Script:Listener) { $Script:Listener.Stop() } } catch { }
    if ($Script:OwnsNgrok -and [bool]$Config.stopNgrokWithCompanion -and $null -ne $Script:NgrokProcess) {
        try { if (-not $Script:NgrokProcess.HasExited) { $Script:NgrokProcess.Kill() } } catch { }
    }
    $Script:NgrokState = 'Stopped'
    try { Write-Status 'Stopped' } catch { }
    Add-Log 'Companion stopped.'
}
