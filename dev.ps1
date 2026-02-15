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
        dotnet run --project apps/Gateway.Functions/Gateway.Functions.csproj
    }
    'demo'
    {
        $uri = 'http://localhost:7071/api/chat'
        $requests = @(
            @{ message = 'Do we have ITEM-123 in warehouse MAD? What is available and ETA?' },
            @{ message = 'Create a draft order for ACME: 10x ITEM-123, ship tomorrow.' },
            @{ message = 'Why is SO-456 delayed and what should I do?' }
        )

        foreach ($req in $requests)
        {
            $body = $req | ConvertTo-Json
            $response = Invoke-RestMethod -Method Post -Uri $uri -Body $body -ContentType 'application/json'
            $response | ConvertTo-Json -Depth 8
        }
    }
    'test'
    {
        dotnet test EclipseErpOpenAiKit.NET.sln
    }
}
