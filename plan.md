# EclipseErpOpenAiKit.NET - plan (MVP)

## Definition of Done

A new user can clone this repo on Windows 11 and run a deterministic end-to-end demo locally:

- Azure Functions host runs locally.
- `/api/chat` triggers at least one ERP connector call to Mock ERP.
- Response includes `correlationId`, `toolCalls`, `evidence`, and `auditRef`.
- No OpenAI key required (offline `FakePlanner`).
- Optional OpenAI mode via `OPENAI_API_KEY`.

## Current phase (2026-02-15)

- Active scope: documentation cleanup and plan/status alignment only.
- All non-documentation engineering work is on hold until explicitly resumed.

## Scenarios (E2E)

1. Inventory availability (read)
2. Draft sales order (draft-only, idempotent)
3. Order exception copilot (summary + reasons with evidence + next actions)

## Key decisions

- Azure Functions (.NET isolated) is the primary host.
- Contract-first connector (OpenAPI stub now; replace with real later).
- Draft-only write posture by default.
- Field allowlists and redaction before AI calls and audit persistence.
- Correlation IDs and audit events for every request.

## Detailed plan

See `docs/plan.md` for milestones and expanded acceptance criteria.

## Implementation status (TDD)

- Completed:
  - Scenario integration tests for all `/api/chat` MVP flows:
    - inventory availability
    - draft sales order with idempotent replay
    - order exception copilot evidence allowlist
  - Governance integration tests:
    - unknown tool blocked by allowlist
    - draft write without idempotency blocked
    - audit payload redaction for sensitive nested fields
  - Correlation propagation implementation:
    - request correlation scope set in chat endpoint
    - incoming `x-correlation-id` is honored when provided by client
    - ERP connector propagates `x-correlation-id` on outbound calls
    - integration assertions verify ERP execution sees request correlation ID
  - ERP HTTP-boundary checks:
    - unit tests verify `HttpErpConnector` sends `x-correlation-id` for inventory, draft order, and order exception requests
  - OpenAI optional mode gate:
    - `OPENAI_MODE` supports `off|emulated|real`
    - `real` mode uses OpenAI planner client path with deterministic fallback on failure
  - Optional summarization:
    - `OPENAI_SUMMARIZE=1` enables order exception summary path
    - real mode summary uses OpenAI client with deterministic fallback on errors
  - Integration coverage for AI mode gates:
    - `OPENAI_MODE=off` scenario validated with deterministic doubles
    - `OPENAI_SUMMARIZE=1` + real mode validated with deterministic OpenAI client double

- Next:
  - On hold:
    - Host migration to Azure Functions (.NET isolated) to align implementation with target architecture.
    - Governance hardening so redaction/allowlist applies before any AI summarization call.
    - Order exception response enrichment with explicit "next actions".
    - Cleanup of unused placeholder classes/files.
  - Resume checks once engineering work restarts:
    - Run full `dotnet test`.
    - Run `dev.ps1 demo`.
    - Close any regressions against MVP acceptance criteria.
