# EclipseErpOpenAiKit.NET

A minimal, production-shaped Epicor Eclipse ERP to OpenAI integration kit for .NET 10.

Current host is ASP.NET Core minimal API for local MVP flow. Azure Functions (.NET 10 isolated) remains the primary target but is currently on hold.

> Not affiliated with Epicor. "Epicor Eclipse" is a trademark of Epicor.

## Source of truth

- Product narrative and demo expectations: `README.md`
- Scenarios, contract, and acceptance criteria: `plan.md`
- Supplemental implementation details: `docs/`

## Why this repo exists

Most ERP + AI demos stop at prompts. This kit focuses on production-shaped fundamentals:

- Contract-first integration with OpenAPI
- Safety controls for writes (allowlist, draft-only, idempotency)
- Governance controls (field allowlists and redaction)
- Auditability and observability (correlation ID and audit records)
- Deterministic offline testing (no OpenAI dependency for default path)

## What `/api/chat` does

1. Planner converts user input into tool calls.
2. Policy layer validates tool allowlist and write protections.
3. `ChatOrchestrator` dispatches each allowed tool call to a dedicated handler.
4. ERP connector executes against Mock ERP by default.
5. Response includes explainable evidence and audit reference.

## Current gateway internals (extensible flow)

- Thin HTTP endpoint in `apps/Gateway.Functions/Program.cs`.
- Orchestration in `apps/Gateway.Functions/Services/ChatOrchestrator.cs`.
- Scenario execution via pluggable handlers in `apps/Gateway.Functions/Services/ChatToolHandlers.cs`:
  - `GetInventoryAvailability`
  - `CreateDraftSalesOrder`
  - `ExplainOrderException`
- Add a new scenario by adding one `IChatToolHandler` implementation + DI registration.

This keeps the public API contract stable while making scenario growth low-risk.

## Operating modes

- Offline default: deterministic `FakePlanner`, no API key required.
- OpenAI optional: set `OPENAI_API_KEY` and wire `OpenAiPlanner`.

## Scenarios

1. Inventory availability (read)
2. Draft sales order (draft-only with idempotency; Infor endpoint: `/orders/draft`)
3. Order exception copilot (summary + governed evidence; Infor endpoint: `/orders/{id}/exception-context`)

## Infor-shaped flow coverage

- OAuth2 client-credentials via `InforTokenClient` with cached tokens.
- Typed `InforApiClient` that injects Bearer auth and `x-correlation-id`, with safe errors and sane timeouts.
- Flow2 draft order via `POST /orders/draft` with idempotent replay returning the same draft result.
- Flow3 order exception via `GET /orders/{id}/exception-context`, evidence allowlist enforced.

## Current status

- Infor-shaped Flow2 + Flow3 integration is implemented (OAuth2, typed API client, idempotent draft writes, evidence allowlist).
- Inventory scenario and existing mock/demo path remain stable during this work.
- On-hold backlog:
  - Host migration to Azure Functions (.NET isolated)
  - Governance hardening before AI summarization calls
  - Explicit "next actions" enrichment in order-exception response
  - Removal of unused placeholder code files

## Stable response contract

Every `/api/chat` response returns:

- `correlationId`
- `answer`
- `toolCalls[]`
- `evidence[]`
- `auditRef`

## Quickstart (Windows)

### Prerequisites

- .NET 10 SDK
- Docker Desktop
- Azure Functions Core Tools (recommended for local host workflow)

### Commands

```powershell
.\dev.ps1 up
.\dev.ps1 run
.\dev.ps1 demo
.\dev.ps1 demo-infor
.\dev.ps1 test
```

### OpenAI mode (optional)

```powershell
$env:OPENAI_API_KEY = "your-key"
$env:OPENAI_MODE = "emulated" # default
$env:OPENAI_SUMMARIZE = "1"    # optional order-exception summary
$env:OPENAI_LOG_PAYLOADS = "1" # temporary diagnostics: logs OpenAI request/response JSON bodies
$env:OPENAI_RETRY_BASE_DELAY_SEC = "1" # adaptive retry base delay
$env:OPENAI_RETRY_MAX_DELAY_SEC = "60" # adaptive retry max delay cap
```

Modes:
- `OPENAI_MODE=emulated` (default): OpenAI planner path enabled but deterministically emulated for demo/offline stability.
- `OPENAI_MODE=real`: OpenAI planner uses tool/function-calling HTTP path and falls back to deterministic planner on failure.
- `OPENAI_MODE=off`: forces deterministic offline planner.
- `OPENAI_SUMMARIZE=1`: enables optional OpenAI-backed order exception summary (falls back to deterministic summary on failure).
- `OPENAI_LOG_PAYLOADS=1`: logs OpenAI request/response payload bodies to console for diagnostics (disable after demo/troubleshooting).
- `OPENAI_RETRY_BASE_DELAY_SEC=1`: base delay for retry backoff when `Retry-After` is not returned.
- `OPENAI_RETRY_MAX_DELAY_SEC=60`: max delay cap for retry backoff.

OpenAI retry behavior:
- If OpenAI response includes `Retry-After`, gateway waits exactly that delay.
- Otherwise, gateway uses exponential backoff with jitter: ~1s, ~2s, ~4s, ~8s, ~16s (plus 0-1s random jitter), capped by `OPENAI_RETRY_MAX_DELAY_SEC`.

If key is not set, offline mode remains active regardless of mode.
