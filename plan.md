# EclipseErpOpenAiKit.NET - plan (MVP)

## Definition of Done

A new user can clone this repo on Windows 11 and run a deterministic end-to-end demo locally:

- Azure Functions host runs locally.
- `/api/chat` triggers at least one ERP connector call to Mock ERP.
- Response includes `correlationId`, `toolCalls`, `evidence`, and `auditRef`.
- No OpenAI key required (offline `FakePlanner`).
- Optional OpenAI mode via `OPENAI_API_KEY`.

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
