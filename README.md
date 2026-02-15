# EclipseErpOpenAiKit.NET

A minimal, production-shaped Epicor Eclipse ERP to OpenAI integration kit for .NET 10, hosted on Azure Functions (isolated).

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
3. ERP connector executes against Mock ERP by default.
4. Response includes explainable evidence and audit reference.

## Operating modes

- Offline default: deterministic `FakePlanner`, no API key required.
- OpenAI optional: set `OPENAI_API_KEY` and wire `OpenAiPlanner`.

## Scenarios

1. Inventory availability (read)
2. Draft sales order (draft-only with idempotency)
3. Order exception copilot (summary, reasons with evidence, next actions)

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
.\dev.ps1 test
```

### OpenAI mode (optional)

```powershell
$env:OPENAI_API_KEY = "your-key"
```

If key is not set, offline mode remains active.
