param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('up', 'run', 'demo', 'test')]
    [string]$Command
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$env:DOTNET_CLI_HOME = $root
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

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
        $uri = 'http://localhost:5000/api/chat'
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

        Write-Host "Starting demo against $uri"
        Write-Host ''

        for ($i = 0; $i -lt $requests.Count; $i++)
        {
            $req = $requests[$i]
            $body = @{ message = $req.message } | ConvertTo-Json
            $step = $i + 1
            Write-Host "[$step/$($requests.Count)] Scenario: $($req.name)"
            Write-Host "[$step/$($requests.Count)] Sending request to Gateway API and waiting for response..."
            $started = Get-Date
            $response = Invoke-RestMethod -Method Post -Uri $uri -Body $body -ContentType 'application/json'
            $elapsedMs = [Math]::Round(((Get-Date) - $started).TotalMilliseconds, 1)
            Write-Host "[$step/$($requests.Count)] Received response in ${elapsedMs}ms"
            $response | ConvertTo-Json -Depth 8
            Write-Host ''
        }
    }
    'test'
    {
        dotnet test EclipseErpOpenAiKit.NET.sln -p:RestoreUseStaticGraphEvaluation=true
    }
}
