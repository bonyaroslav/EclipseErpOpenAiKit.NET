param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('up', 'run', 'demo', 'demo-infor', 'test')]
    [string]$Command
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$env:DOTNET_CLI_HOME = $root
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

function Invoke-DemoRequests([string]$Uri)
{
    $requests = @(
        @{
            name = 'Inventory availability check'
            message = 'Do we have ITEM-123 in warehouse MAD? What is available and ETA?'
        },
        @{
            name = 'Draft sales order creation'
            message = 'Create a draft order for ACME: 10x ITEM-123, ship tomorrow.'
        },
        @{
            name = 'Order exception explanation'
            message = 'Why is SO-456 delayed and what should I do?'
        }
    )

    Write-Host "Starting demo against $Uri"
    Write-Host ''

    for ($i = 0; $i -lt $requests.Count; $i++)
    {
        $req = $requests[$i]
        $body = @{ message = $req.message } | ConvertTo-Json
        $step = $i + 1
        Write-Host "[$step/$($requests.Count)] Scenario: $($req.name)"
        Write-Host "[$step/$($requests.Count)] Sending request to Gateway API and waiting for response..."
        $started = Get-Date
        $response = Invoke-RestMethod -Method Post -Uri $Uri -Body $body -ContentType 'application/json'
        $elapsedMs = [Math]::Round(((Get-Date) - $started).TotalMilliseconds, 1)
        Write-Host "[$step/$($requests.Count)] Received response in ${elapsedMs}ms"
        $response | ConvertTo-Json -Depth 8
        Write-Host ''
    }
}

function Wait-ForHealth([string]$HealthUri, [int]$MaxAttempts = 60)
{
    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++)
    {
        try
        {
            $response = Invoke-RestMethod -Method Get -Uri $HealthUri -TimeoutSec 2
            if ($response.ok -eq $true)
            {
                return $true
            }
        }
        catch
        {
            Start-Sleep -Seconds 1
        }
    }

    return $false
}

switch ($Command)
{
    'up'
    {
        docker compose up -d --build
    }
    'run'
    {
        dotnet run --project apps/Gateway.Functions/Gateway.Functions.csproj -p:RestoreUseStaticGraphEvaluation=true
    }
    'demo'
    {
        Invoke-DemoRequests -Uri 'http://localhost:5000/api/chat'
    }
    'demo-infor'
    {
        $gatewayUrl = 'http://localhost:5001'
        $gatewayHealthUrl = "$gatewayUrl/health"
        $gatewayApiUrl = "$gatewayUrl/api/chat"

        $inforBaseUrl = if ([string]::IsNullOrWhiteSpace($env:INFOR_BASE_URL)) { 'http://localhost:5080' } else { $env:INFOR_BASE_URL }
        $inforClientId = if ([string]::IsNullOrWhiteSpace($env:INFOR_CLIENT_ID)) { 'demo-client' } else { $env:INFOR_CLIENT_ID }
        $inforClientSecret = if ([string]::IsNullOrWhiteSpace($env:INFOR_CLIENT_SECRET)) { 'demo-secret' } else { $env:INFOR_CLIENT_SECRET }

        $previousEnv = @{
            ERP_MODE = $env:ERP_MODE
            INFOR_BASE_URL = $env:INFOR_BASE_URL
            INFOR_CLIENT_ID = $env:INFOR_CLIENT_ID
            INFOR_CLIENT_SECRET = $env:INFOR_CLIENT_SECRET
            ASPNETCORE_URLS = $env:ASPNETCORE_URLS
        }

        $gatewayOutput = Join-Path $root '.tmp.gateway.out.log'
        $gatewayError = Join-Path $root '.tmp.gateway.err.log'
        Remove-Item $gatewayOutput -ErrorAction SilentlyContinue
        Remove-Item $gatewayError -ErrorAction SilentlyContinue

        $gatewayProcess = $null
        try
        {
            $env:ERP_MODE = 'infor'
            $env:INFOR_BASE_URL = $inforBaseUrl
            $env:INFOR_CLIENT_ID = $inforClientId
            $env:INFOR_CLIENT_SECRET = $inforClientSecret
            $env:ASPNETCORE_URLS = $gatewayUrl

            Write-Host "Starting Gateway in Infor mode against $inforBaseUrl ..."
            $gatewayProcess = Start-Process -FilePath 'dotnet' `
                -ArgumentList @('run', '--project', 'apps/Gateway.Functions/Gateway.Functions.csproj', '-p:RestoreUseStaticGraphEvaluation=true') `
                -WorkingDirectory $root `
                -PassThru `
                -RedirectStandardOutput $gatewayOutput `
                -RedirectStandardError $gatewayError

            if (-not (Wait-ForHealth -HealthUri $gatewayHealthUrl))
            {
                throw "Gateway did not become healthy at $gatewayHealthUrl. Check $gatewayOutput and $gatewayError."
            }

            Invoke-DemoRequests -Uri $gatewayApiUrl
        }
        finally
        {
            if ($null -ne $gatewayProcess -and -not $gatewayProcess.HasExited)
            {
                Stop-Process -Id $gatewayProcess.Id -Force
            }

            foreach ($pair in $previousEnv.GetEnumerator())
            {
                if ($null -eq $pair.Value)
                {
                    Remove-Item "Env:$($pair.Key)" -ErrorAction SilentlyContinue
                }
                else
                {
                    Set-Item "Env:$($pair.Key)" $pair.Value
                }
            }
        }
    }
    'test'
    {
        dotnet test EclipseErpOpenAiKit.NET.sln -p:RestoreUseStaticGraphEvaluation=true
    }
}
