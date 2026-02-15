# plan.md — EclipseAI Gateway Kit (.NET 10) + “Order Exception Copilot” scenario

## Goal (Definition of Done)
A new user can clone the repo on Windows 11 and run:
1) a **deterministic end-to-end test** (offline, no OpenAI key, no Azure subscription)
2) a **one-command interactive demo** that shows the same flows
3) an **optional OpenAI-powered demo mode** (tool calling + natural-language outputs)

Primary host: **Azure Functions (.NET 10 isolated)**  
Default backend: **Mock ERP** (swap to real Eclipse/OpenAPI later)

References (for credibility in docs/readme):
- Run Azure Functions locally: https://learn.microsoft.com/en-us/azure/azure-functions/functions-run-local
- Azurite (Azure Storage emulator): https://learn.microsoft.com/en-us/azure/storage/common/storage-use-azurite
- OpenAI tool/function calling: https://platform.openai.com/docs/guides/function-calling
- OpenAI data controls guidance: https://platform.openai.com/docs/guides/your-data

---

## Decisions (locked)
- **Offline-first**: tests never call OpenAI; demo works without any external accounts.
- **OpenAI is still first-class**: runtime supports OpenAI tool calling for planning and summarization; enabled via env var.
- **Contract-first**: OpenAPI contract stored in `/contracts`; typed client generated (pinned tooling).
- **Safety posture**:
  - Writes are **draft-only** by default
  - Tool allowlist enforced server-side
  - Idempotency required for draft writes
- **Governance posture**:
  - Field allowlists per tool
  - Redaction before any AI call and before audit persistence
- **Observability**:
  - Correlation IDs are mandatory
  - Audit events for each tool execution + ERP call

---

## End-to-end scenarios (what the demo + E2E test must prove)

### Scenario 1 — Inventory Availability (Read)
**User message (example)**
- “Do we have ITEM-123 in warehouse MAD? What’s available and ETA?”

**Tools used**
- `GetInventoryAvailability(itemId, warehouseId)`

**ERP calls (Mock)**
- `GET /inventory/{itemId}?warehouse={warehouseId}`

**Expected result**
- Response includes:
  - `answer` (human readable)
  - `correlationId`
  - `toolCalls` (at least one)
  - `evidence` (selected ERP fields used)
- Audit contains:
  - correlationId
  - tool name
  - ERP endpoint(s)
  - status + latency

---

### Scenario 2 — Draft Sales Order (Write, guarded)
**User message (example)**
- “Create a draft order for ACME: 10x ITEM-123, ship tomorrow.”

**Tools used**
- `CreateDraftSalesOrder(customerId, lines[], shipDate, idempotencyKey)`

**Policies enforced**
- Must be allowlisted
- Must be “draft-only”
- Must include idempotencyKey

**ERP calls (Mock)**
- `POST /draftSalesOrders`

**Expected result**
- Response clearly says “draft created” + returns draft id
- “Commit” is not performed (and is disabled by default)
- If same request repeats with same idempotencyKey → same draft id (or safe “already created” behavior)

---

### Scenario 3 — Order Exception Copilot (WOW scenario)
**User message (example)**
- “Why is SO-456 delayed and what should I do?”  
  Optional: “I’m customer support” vs “I’m warehouse”

**Tools used**
- `ExplainOrderException(orderId, role)` (or a tool orchestration that internally calls sub-tools)

**ERP calls (Mock)**
- `GET /salesOrders/{id}`
- `GET /salesOrders/{id}/holds`
- `GET /salesOrders/{id}/lines`
- `GET /inventory/{sku}?warehouse=...`
- `GET /customers/{id}/arSummary` (minimal: overdueDays + status flag, no sensitive details)

**Expected result**
- Response sections (stable format for demo + tests):
  1) **Summary** (2–3 bullets, role-aware wording)
  2) **Reasons with evidence** (each reason references specific fields)
  3) **Next actions** (top 3, safe and bounded)
  4) Optional **draft artifacts** (text-only: customer update message)
- Governance proof:
  - Only allowlisted fields appear in `evidence`
  - PII/margins are not present

---

## Acceptance criteria

### A) “Impress the Epicor Eclipse client” acceptance
- **E2E demo is runnable without their Eclipse instance** (Mock ERP default)
- **Swap path is obvious**:
  - `contracts/eclipse.sample.openapi.json` → `contracts/eclipse.openapi.json`
  - regenerate client
  - update connector base URL + auth provider
- **Safety is real**:
  - draft-only enforcement is visible in logs/response
  - idempotency demonstrated
- **Auditability is real**:
  - every request produces an audit record with correlationId

### B) “Impress other ERP clients” acceptance
- Clear “adapter pattern” section in docs:
  - `IErpConnector` interface + minimal implementation steps
  - the three scenarios are ERP-agnostic (inventory, draft order, exception copilot)
- OpenAPI-driven client generation is generic:
  - works with any ERP exposing Swagger/OpenAPI

### C) “Impress engineers / reusability” acceptance
- **One-command dev UX**
  - `dev.ps1 up`    → docker compose (Mock ERP + Azurite)
  - `dev.ps1 run`   → Functions host
  - `dev.ps1 demo`  → runs the three scenarios (prints correlationId + audit path)
  - `dev.ps1 test`  → runs all tests
- **Deterministic tests**
  - Unit tests: policies, governance, idempotency, session-refresh simulation
  - Contract tests: Mock ERP responses conform to OpenAPI schemas
  - Integration tests: exercise Functions → Mock ERP → Audit
- **No external dependencies for CI**
  - Offline path uses `FakePlanner`
  - OpenAI tests are opt-in, manual (or tagged and skipped by default)

---

## Implementation milestones (minimal but complete)

### M0 — Repo skeleton + dev scripts
Deliver:
- Solution layout (`apps/`, `src/`, `mocks/`, `tests/`, `contracts/`, `docs/`)
- `dev.ps1` with: `up/run/demo/test`
- docker compose: Mock ERP + Azurite

Done when:
- `dev.ps1 up` and `dev.ps1 run` work on a clean Windows machine.

### M1 — Contract-first + Mock ERP
Deliver:
- Minimal OpenAPI contract for endpoints used by the 3 scenarios
- NSwag (or equivalent) pinned tool config + generated client project
- Mock ERP implements the contract

Done when:
- Contract tests validate schema conformance (responses match OpenAPI).

### M2 — Domain policies + governance
Deliver:
- Tool registry + allowlist enforcement
- Draft-only + idempotency enforcement
- Field allowlists + redaction pipeline
- Unit tests for the above

Done when:
- Unit tests cover deny/allow cases + redaction snapshot.

### M3 — Azure Functions host + deterministic E2E proof
Deliver:
- `/api/chat` function
- Planner abstraction:
  - `FakePlanner` default (offline)
  - `OpenAiPlanner` optional (tool calling + summarization)
- Integration tests for the three scenarios:
  - calls endpoint
  - asserts expected toolCalls
  - asserts ERP calls happened
  - asserts audit record exists with correlationId

Done when:
- `dev.ps1 test` passes and produces stable output.

### M4 — Demo polish (what sells)
Deliver:
- `examples/requests.http` (or Postman collection)
- `/docs/demo.gif` or terminal recording
- README section: “Offline demo” and “OpenAI demo mode”

Done when:
- a reviewer can validate value in < 3 minutes.

---

## E2E output contract (stable, demo-friendly)
All `/api/chat` responses must include:
- `correlationId` (string)
- `answer` (string)
- `toolCalls[]` (name + args)
- `evidence[]` (field-level references, allowlisted)
- `auditRef` (path/id)

This output structure is what makes the demo “enterprise”: it’s explainable, auditable, and testable.

---

## Docs to include (small, high impact)
- `docs/decisions.md` — why offline-first, why draft-only, why contract-first
- `docs/threat-model.md` — injection, exfiltration, unsafe writes, secrets, audit retention
- `docs/adding-a-new-erp.md` — adapter steps + where to plug OpenAPI + policies
