# Flow2 + Flow3 TDD Plan (Infor Integration)

## Goal
Implement only Flow2 CreateDraftSalesOrder and Flow3 ExplainOrderException using Infor OAuth2 + API client. Preserve existing mock/demo path and the inventory scenario. All tests must be offline and deterministic.

## Non-goals
Do not remove or break the current mock ERP demo flow.
Do not migrate hosts to Azure Functions in this work.
Do not add new production dependencies unless they directly support the required tests or demo.

## Assumptions
Infor OAuth2 endpoint: `POST /oauth/token` (client credentials).
Infor draft endpoint: `POST /orders/draft` -> `{ draftId, externalOrderNumber, status: "DRAFT" }`.
Infor exception endpoint: `GET /orders/{orderId}/exception-context` -> allowlisted fields plus extra fields.

## TDD Iterations
1. Unit: InforTokenClient caching and refresh.
Add tests for caching until expiry, refresh after expiry, and no secret logging.
Implement `InforTokenClient` with in-memory cache, expiry skew, and safe logging.
2. Unit: InforApiClient headers and error handling.
Add tests for `Authorization: Bearer` and `x-correlation-id`, plus non-2xx -> typed exception with safe message.
Verify no retry on `POST` and set a sane timeout.
Implement `InforApiClient` as a typed HttpClient that injects token and correlation headers.
3. Unit: Connector mapping for Flow2/Flow3.
Add tests for calling `/orders/draft` and `/orders/{id}/exception-context` with expected payloads.
Implement a minimal connector (new or updated) that uses `InforApiClient` and DTOs aligned with required shapes.
4. Unit: Flow2 argument parsing and idempotency.
Add tests for missing `idempotencyKey`, required `requestedDate`, and line parsing (`item`, `qty`, `unitPrice`).
Add test that replay with same `idempotencyKey` returns the same draft result without a second downstream call.
Update ToolArgReaders, DTOs, and `IdempotencyCache` payload hash to include new fields and to replay cached draft results.
5. Unit: Flow3 evidence allowlist.
Add test that extra fields from the exception endpoint never appear in evidence.
Update allowlist and governed-data filtering as needed.
6. Integration: in-process fake Infor HTTP server.
Add a test server with `/oauth/token`, `/orders/draft`, and `/orders/{id}/exception-context`.
Verify token exchange and downstream call counts are correct.
7. E2E: `/api/chat` using FakePlanner.
Flow2 create + replay -> only one downstream `POST /orders/draft`.
Flow3 -> response evidence includes only allowlisted keys.
8. Demo doc.
Create `DEMO.md` with `dotnet test` instructions and one example request per flow.

## Files Likely Touched
`src/EclipseAi.Connectors.Erp/ErpConnector.cs`
`src/EclipseAi.Connectors.Erp/` (new `InforTokenClient`, `InforApiClient`, DTOs)
`apps/Gateway.Functions/Services/ChatToolHandlers.cs`
`apps/Gateway.Functions/Services/AuditStore.cs`
`apps/Gateway.Functions/Program.cs`
`tests/Unit/EclipseAi.Tests.Unit/UnitTest1.cs`
`tests/Integration/EclipseAi.Tests.Integration/ChatScenariosTests.cs`
`tests/Integration/EclipseAi.Tests.Integration/ChatOrchestratorTests.cs`
`DEMO.md`

## Acceptance Checks
`dotnet test` passes.
Existing mock/demo path unchanged.
Idempotency verified with a single downstream POST for replay.
Evidence allowlist verified.
No secrets in logs or audit, and correlation ID propagates end to end.
