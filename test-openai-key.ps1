# Example usage:
# $env:OPENAI_API_KEY="sk-..."; .\test-openai-key.ps1

param(
  [int]$AttemptCount = 10,
  [int]$TimeoutSec = 30,
  [double]$BaseDelaySec = 1,
  [double]$MaxDelaySec = 60
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($env:OPENAI_API_KEY)) {
  throw "OPENAI_API_KEY is missing in this shell"
}

# Force TLS 1.2 for older Windows PowerShell hosts.
try {
  if ($PSVersionTable.PSEdition -ne "Core") {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
  }
} catch {
}

$uri = "https://api.openai.com/v1/responses"
$headers = @{
  Authorization = "Bearer $($env:OPENAI_API_KEY)"
  "Content-Type" = "application/json"
}

$body = @{
  model = "gpt-5-mini"
  input = "Reply with exactly: OK"
} | ConvertTo-Json -Depth 5

function Get-HeaderValue {
  param(
    $Headers,
    [string]$Name
  )

  if (-not $Headers) { return $null }

  try {
    foreach ($k in $Headers.Keys) {
      if ($k -ieq $Name) {
        $v = $Headers[$k]
        if ($v -is [System.Array]) { return ($v -join ",") }
        return [string]$v
      }
    }
  } catch {
  }

  return $null
}

function Get-ErrorDetails {
  param($ErrorRecord)

  $ex = $ErrorRecord.Exception
  $response = $null
  $statusCode = $null
  $statusText = $null
  $responseBody = $null
  $responseHeaders = @{}

  if ($ex.PSObject.Properties["Response"]) {
    $response = $ex.Response
  }

  if ($response) {
    # HttpWebResponse path (Windows PowerShell) and HttpResponseMessage path (PowerShell 7)
    try {
      if ($response.PSObject.Properties["StatusCode"]) {
        $rawStatus = $response.StatusCode
        if ($rawStatus -is [int]) { $statusCode = $rawStatus } else { $statusCode = [int]$rawStatus.value__ }
      }
    } catch {
    }

    try {
      if ($response.PSObject.Properties["ReasonPhrase"]) {
        $statusText = [string]$response.ReasonPhrase
      } elseif ($response.PSObject.Properties["StatusDescription"]) {
        $statusText = [string]$response.StatusDescription
      }
    } catch {
    }

    try {
      if ($response.PSObject.Properties["Headers"]) {
        $rawHeaders = $response.Headers
        if ($rawHeaders) {
          foreach ($key in $rawHeaders.Keys) {
            $value = $rawHeaders[$key]
            if ($value -is [System.Array]) {
              $responseHeaders[$key] = ($value -join ",")
            } else {
              $responseHeaders[$key] = [string]$value
            }
          }
        }
      }
    } catch {
    }

    try {
      if ($ErrorRecord.ErrorDetails -and $ErrorRecord.ErrorDetails.Message) {
        $responseBody = [string]$ErrorRecord.ErrorDetails.Message
      } elseif ($response.PSObject.Methods["GetResponseStream"]) {
        $stream = $response.GetResponseStream()
        if ($stream) {
          $reader = New-Object System.IO.StreamReader($stream)
          $responseBody = $reader.ReadToEnd()
          $reader.Dispose()
          $stream.Dispose()
        }
      }
    } catch {
    }
  }

  [PSCustomObject]@{
    ExceptionType = $ex.GetType().FullName
    Message = $ex.Message
    InnerMessage = $(if ($ex.InnerException) { $ex.InnerException.Message } else { $null })
    StatusCode = $statusCode
    StatusText = $statusText
    Headers = $responseHeaders
    Body = $responseBody
  }
}

function Get-DelaySeconds {
  param(
    [int]$FailureIndex,
    $Headers,
    [double]$InitialDelaySec,
    [double]$DelayCapSec
  )

  $retryAfter = Get-HeaderValue -Headers $Headers -Name "Retry-After"
  if ($retryAfter) {
    $seconds = 0.0
    if ([double]::TryParse($retryAfter, [ref]$seconds)) {
      return [Math]::Min([Math]::Max($seconds, 0.2), $DelayCapSec)
    }

    try {
      $until = [DateTimeOffset]::Parse($retryAfter)
      $delta = ($until - [DateTimeOffset]::UtcNow).TotalSeconds
      return [Math]::Min([Math]::Max($delta, 0.2), $DelayCapSec)
    } catch {
    }
  }

  $exp = [Math]::Pow(2, [Math]::Max($FailureIndex - 1, 0))
  $delay = [Math]::Min($InitialDelaySec * $exp, $DelayCapSec)
  $jitter = (Get-Random -Minimum 0.0 -Maximum 1.0)
  return [Math]::Round($delay + $jitter, 2)
}

Write-Host "Single call sanity check..."
try {
  $first = Invoke-WebRequest -Method Post -Uri $uri -Headers $headers -Body $body -TimeoutSec $TimeoutSec
  $firstPayload = $first.Content | ConvertFrom-Json
  Write-Host ("Status: {0}" -f $first.StatusCode)
  Write-Host ("Request-Id: {0}" -f (Get-HeaderValue -Headers $first.Headers -Name "x-request-id"))
  Write-Host ("Output: {0}" -f $firstPayload.output_text)
} catch {
  $d = Get-ErrorDetails -ErrorRecord $_
  Write-Host "Sanity check: FAIL"
  Write-Host ("  Exception : {0}" -f $d.ExceptionType)
  Write-Host ("  Message   : {0}" -f $d.Message)
  if ($d.InnerMessage) { Write-Host ("  Inner     : {0}" -f $d.InnerMessage) }
  if ($d.StatusCode) { Write-Host ("  HTTP      : {0} {1}" -f $d.StatusCode, $d.StatusText) } else { Write-Host "  HTTP      : n/a (transport-level failure before response)" }

  $reqId = Get-HeaderValue -Headers $d.Headers -Name "x-request-id"
  $retryAfter = Get-HeaderValue -Headers $d.Headers -Name "Retry-After"
  Write-Host ("  request_id: {0}" -f $(if ($reqId) { $reqId } else { "n/a" }))
  if ($retryAfter) { Write-Host ("  Retry-After: {0}" -f $retryAfter) }

  if ($d.Body) {
    $bodyOneLine = ($d.Body -replace "`r", " " -replace "`n", " ").Trim()
    Write-Host ("  Body      : {0}" -f $bodyOneLine)
  }
}

Write-Host ""
Write-Host ("Intermittency test ({0} attempts)..." -f $AttemptCount)

$failureIndex = 0
for ($attempt = 1; $attempt -le $AttemptCount; $attempt++) {
  try {
    $resp = Invoke-WebRequest -Method Post -Uri $uri -Headers $headers -Body $body -TimeoutSec $TimeoutSec
    $reqId = Get-HeaderValue -Headers $resp.Headers -Name "x-request-id"
    Write-Host ("Attempt {0}: OK (HTTP {1}, request_id={2})" -f $attempt, $resp.StatusCode, $(if ($reqId) { $reqId } else { "n/a" }))
  } catch {
    $failureIndex++
    $d = Get-ErrorDetails -ErrorRecord $_

    Write-Host ("Attempt {0}: FAIL" -f $attempt)
    Write-Host ("  Exception : {0}" -f $d.ExceptionType)
    Write-Host ("  Message   : {0}" -f $d.Message)
    if ($d.InnerMessage) { Write-Host ("  Inner     : {0}" -f $d.InnerMessage) }
    if ($d.StatusCode) { Write-Host ("  HTTP      : {0} {1}" -f $d.StatusCode, $d.StatusText) } else { Write-Host "  HTTP      : n/a (transport-level failure before response)" }

    $reqId = Get-HeaderValue -Headers $d.Headers -Name "x-request-id"
    $retryAfter = Get-HeaderValue -Headers $d.Headers -Name "Retry-After"
    $limitRemReq = Get-HeaderValue -Headers $d.Headers -Name "x-ratelimit-remaining-requests"
    $limitResetReq = Get-HeaderValue -Headers $d.Headers -Name "x-ratelimit-reset-requests"

    Write-Host ("  request_id: {0}" -f $(if ($reqId) { $reqId } else { "n/a" }))
    if ($retryAfter) { Write-Host ("  Retry-After: {0}" -f $retryAfter) }
    if ($limitRemReq) { Write-Host ("  RL rem req : {0}" -f $limitRemReq) }
    if ($limitResetReq) { Write-Host ("  RL reset   : {0}" -f $limitResetReq) }

    if ($d.Body) {
      $bodyOneLine = ($d.Body -replace "`r", " " -replace "`n", " ").Trim()
      Write-Host ("  Body      : {0}" -f $bodyOneLine)
    }

    if ($attempt -lt $AttemptCount) {
      $delaySec = Get-DelaySeconds -FailureIndex $failureIndex -Headers $d.Headers -InitialDelaySec $BaseDelaySec -DelayCapSec $MaxDelaySec
      Write-Host ("  Sleeping  : {0}s before next attempt" -f $delaySec)
      Start-Sleep -Milliseconds ([int]($delaySec * 1000))
    }
  }
}
