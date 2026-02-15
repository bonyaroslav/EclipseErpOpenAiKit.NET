# EclipseErpOpenAiKit.NET â€” plan.md (MVP)

## Definition of Done
A new user can clone this repo on Windows 11 and run a deterministic end-to-end demo locally:
- Azure Functions host runs locally
- /api/chat triggers at least one ERP connector call to Mock ERP
- response includes correlationId + toolCalls + evidence + auditRef
- no OpenAI key required (offline FakePlanner)
- optional OpenAI mode via OPENAI_API_KEY

## Scenarios (E2E)
1) Inventory availability (read)
2) Draft sales order (draft-only, idempotent)
3) Order Exception Copilot (summary + reasons w/ evidence + next actions)

## Key decisions
- Azure Functions (.NET isolated) is the primary host
- Contract-first connector (OpenAPI stub now; replace with real later)
- Draft-only write posture by default
- Field allowlists + redaction before any AI call/audit
- Correlation IDs + audit events for every request
