# EclipseErpOpenAiKit.NET
A minimal, production-shaped **Epicor Eclipse ERP ↔ ChatGPT/OpenAI** integration kit for **.NET 10**, hosted on **Azure Functions (isolated)**.

It includes a **one-command local end-to-end demo** using a **Mock Eclipse ERP**, plus deterministic **unit/contract/integration tests**. Flip one env var and the same flows use **real OpenAI tool/function calling** for planning (and optionally summarization).

> Not affiliated with Epicor. “Epicor Eclipse” is a trademark of Epicor.

## Why this repo exists
Most “ERP + ChatGPT” demos stop at prompts. Real integrations need:
- **Contract-first** integration (OpenAPI → typed clients)
- **Session/auth lifecycle** abstraction (Eclipse-shaped)
- **Safety** for writes (draft-first + allowlists + idempotency)
- **Data governance** (field allowlists + redaction)
- **Auditability + observability** (correlation IDs, audit events, structured logs)
- **Deterministic tests** you can run anywhere

This kit is intentionally small and focuses on proving those fundamentals end-to-end.

## What the demo actually does
You POST a message to `/api/chat`:
1) A **Planner** converts natural language into a **tool call** (e.g., `GetInventoryAvailability(...)`).
2) The gateway validates policies (allowlist, draft-only, idempotency).
3) It calls the **ERP connector** (Mock Eclipse by default).
4) It returns an answer plus **evidence + audit reference**.

### OpenAI in this kit (two modes)
- **Offline Demo Mode (default):** uses a deterministic `FakePlanner` so demo/tests run with **no API key**, no cost, no flaky dependencies.
- **OpenAI Mode:** uses `OpenAiPlanner` with **tool/function calling** to plan tool calls from real user input (optional: also summarizes ERP payloads).

## End-to-end scenarios (thin slice + WOW)
The repo ships three scenarios (details in `plan.md`):
1) Inventory availability (read)
2) Draft sales order (draft-only write + idempotency)
3) **Order Exception Copilot** (summary + reasons with evidence + next actions)

## Response contract (stable + demo-friendly)
Every `/api/chat` response includes:
- `correlationId`
- `answer`
- `toolCalls[]` (name + args)
- `evidence[]` (allowlisted fields only)
- `auditRef`

(Full acceptance criteria and exact scenario specs are in `plan.md`.)

---

## Quickstart (local demo on Windows)
### Prerequisites
- .NET 10 SDK
- Docker Desktop
- Azure Functions Core Tools (to run Functions locally)

### Run the demo (offline)
```powershell
.\dev.ps1 up      # starts Mock ERP + local deps
.\dev.ps1 run     # starts Azure Functions host
.\dev.ps1 demo    # runs 3 scenario calls end-to-end
.\dev.ps1 test    # unit + contract + integration tests
