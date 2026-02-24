# Demo (Flow2 + Flow3)

## Tests

```powershell
dotnet test
```

## Run Gateway + Mock ERP

```powershell
.\dev.ps1 up
.\dev.ps1 run
```

## Flow2: CreateDraftSalesOrder

```powershell
$body = @{ message = "Create a draft order for ACME: 10x ITEM-123, ship tomorrow." } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri http://localhost:5000/api/chat -ContentType "application/json" -Body $body
```

## Flow3: ExplainOrderException

```powershell
$body = @{ message = "Why is SO-456 delayed and what should I do?" } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri http://localhost:5000/api/chat -ContentType "application/json" -Body $body
```

## Optional: Infor Mode Against Mock ERP

```powershell
$env:ERP_MODE = "infor"
$env:INFOR_BASE_URL = "http://localhost:5080"
$env:INFOR_CLIENT_ID = "demo-client"
$env:INFOR_CLIENT_SECRET = "demo-secret"
.\dev.ps1 run
```
