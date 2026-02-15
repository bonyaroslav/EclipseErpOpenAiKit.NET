# Adding a New ERP

## Goal

Swap the Mock ERP connector with a real ERP adapter while preserving `/api/chat` contract and safety policies.

## Steps

1. Place OpenAPI document at `contracts/<your-erp>.openapi.json`.
2. Generate or implement a typed client for required scenario endpoints.
3. Implement `IErpConnector` in `src/EclipseAi.Connectors.Erp` (or a sibling connector project).
4. Keep tool names and argument shapes stable for planner compatibility.
5. Enforce governance in gateway flow:
   - tool allowlist
   - draft-only write behavior
   - idempotency on draft writes
   - field allowlists + redaction before AI and audit
6. Wire connector base URL/auth configuration in app settings or environment.
7. Run `./dev.ps1 test` and validate all three scenarios.

## Required scenario coverage

- Inventory availability (read)
- Draft sales order (guarded write)
- Order exception copilot (evidence-backed summary)
