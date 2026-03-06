# 1. Project summary

This repository appears to be a minimal, production-shaped ERP-to-OpenAI integration kit focused on Epicor Eclipse-style scenarios, implemented in .NET 10 with an HTTP gateway, ERP connector layer, governance controls, and deterministic offline AI behavior. The current runnable host is an ASP.NET Core minimal API in `apps/Gateway.Functions/Program.cs`, while `README.md`, `plan.md`, and `apps/Gateway.Functions/local.settings.example.json` show Azure Functions isolated as the intended primary host target.

Problem it tries to solve:
- Turn natural-language requests into governed ERP actions and explainable responses without making the demo or tests depend on live OpenAI.
- Provide a stable `/api/chat` contract that can sit in front of ERP operations such as availability lookup, guarded draft order creation, and order exception explanation.

What it is:
- Best described as a reference implementation / internal-style integration kit, not a generic connector package.
- It includes a runnable gateway, a mock ERP service, an Infor-shaped ERP adapter path, contract artifacts, demo scripts, and tests.

Main technical scope:
- Planner-driven chat orchestration
- ERP connector abstraction with mock HTTP and Infor-shaped implementations
- Tool allowlisting, draft-only write posture, idempotency enforcement
- Redaction and evidence allowlisting
- Correlation propagation and audit persistence
- Deterministic offline testing and optional OpenAI function-calling / summarization

# 2. Core integration capabilities

## Inventory availability lookup
- What it does: Executes `GetInventoryAvailability` and calls ERP inventory endpoints to return quantity and ETA evidence.
- Why it matters: Demonstrates a read-only ERP query path from natural language through tool selection to connector execution.
- Evidence:
  - `apps/Gateway.Functions/Services/ChatToolHandlers.cs` - `InventoryToolHandler.ExecuteAsync`
  - `src/EclipseAi.Connectors.Erp/ErpConnector.cs` - `IErpConnector.GetInventoryAsync`, `HttpErpConnector.GetInventoryAsync`, `InforErpConnector.GetInventoryAsync`
  - `mocks/Mock.Erp/Program.cs` - `GET /inventory/{itemId}`
  - `tests/Integration/EclipseAi.Tests.Integration/ChatScenariosTests.cs` - `InventoryAvailabilityScenario_ReturnsContractAndEvidence`
  - `tests/Unit/EclipseAi.Tests.Unit/UnitTest1.cs` - `GetInventoryAsync_AddsCorrelationHeader`
- Confidence: high

## Draft sales order creation with idempotent replay
- What it does: Executes `CreateDraftSalesOrder`, validates args, requires `idempotencyKey`, writes a draft order only, and replays the same result for repeated requests with the same payload.
- Why it matters: This is the strongest ERP-write maturity signal in the repo because it combines write gating, argument validation, payload hashing, and replay-safe behavior.
- Evidence:
  - `apps/Gateway.Functions/Services/ChatToolHandlers.cs` - `DraftSalesOrderToolHandler.ExecuteAsync`
  - `src/EclipseAi.Governance/Governance.cs` - `ToolPolicy.IsDraftWriteAllowed`
  - `apps/Gateway.Functions/Services/AuditStore.cs` - `IdempotencyCache`, `ComputePayloadHash`, `ReserveDraft`, `CompleteDraft`, `FailDraft`
  - `src/EclipseAi.Connectors.Erp/ErpConnector.cs` - `IErpConnector.CreateDraftOrderAsync`, `HttpErpConnector.CreateDraftOrderAsync`, `InforErpConnector.CreateDraftOrderAsync`
  - `mocks/Mock.Erp/Program.cs` - `POST /draftSalesOrders`, `POST /orders/draft`
  - `tests/Integration/EclipseAi.Tests.Integration/ChatScenariosTests.cs` - `DraftSalesOrderScenario_IsIdempotentForSamePlannerKey`, `DraftSalesOrderScenario_Infor_IsIdempotent`
  - `tests/Integration/EclipseAi.Tests.Integration/ChatScenariosTests.cs` - `DraftWithoutIdempotency_IsBlockedByPolicy`, `DraftWithSameIdempotencyKeyDifferentPayload_IsBlocked`
  - `tests/Integration/EclipseAi.Tests.Integration/ChatOrchestratorTests.cs` - `HandleAsync_DraftFlow_ReplaysIdempotently_ForSamePayload`
- Confidence: high

## Order exception context retrieval with governed evidence
- What it does: Executes `ExplainOrderException`, fetches exception context from ERP, allowlists specific evidence fields, redacts sensitive data, and returns summary text plus evidence.
- Why it matters: This is the clearest example of ERP + AI boundary control in the repo. The handler does not blindly pass ERP payloads through.
- Evidence:
  - `apps/Gateway.Functions/Services/ChatToolHandlers.cs` - `ExplainOrderExceptionToolHandler.ExecuteAsync`
  - `apps/Gateway.Functions/Services/ChatToolHandlers.cs` - `BuildGovernedOrderExceptionData`, `IsAllowlistedEvidenceField`
  - `src/EclipseAi.Connectors.Erp/ErpConnector.cs` - `IErpConnector.GetOrderExceptionContextAsync`, `InforErpConnector.GetOrderExceptionContextAsync`
  - `mocks/Mock.Erp/Program.cs` - `GET /orders/{orderId}/exception-context`, `GET /orderException/{orderId}`
  - `tests/Integration/EclipseAi.Tests.Integration/ChatScenariosTests.cs` - `OrderExceptionScenario_UsesAllowlistedEvidenceOnly`, `OrderExceptionScenario_Infor_UsesAllowlistedEvidenceOnly`
  - `tests/Integration/EclipseAi.Tests.Integration/ChatScenariosTests.cs` - `OpenAiSummarizeEnabled_UsesInjectedDeterministicSummary`
- Confidence: high

## Infor-shaped ERP API client with OAuth2 client credentials
- What it does: Uses a token client plus typed API client to call Infor-style endpoints with bearer auth and correlation headers.
- Why it matters: Shows the repo is not limited to a local mock; it has a concrete external-ERP integration shape with auth and header handling.
- Evidence:
  - `src/EclipseAi.Connectors.Erp/ErpConnector.cs` - `InforTokenClient`, `InforApiClient`, `InforErpConnector`
  - `apps/Gateway.Functions/Program.cs` - conditional DI for `ERP_MODE=infor`
  - `tests/Unit/EclipseAi.Tests.Unit/UnitTest1.cs` - `TokenClient_CachesToken_UntilExpiry`, `TokenClient_RefreshesAfterExpiry`, `TokenClient_Error_DoesNotLeakSecret`
  - `tests/Unit/EclipseAi.Tests.Unit/UnitTest1.cs` - `InforApiClient_AddsBearerAndCorrelationHeaders`, `InforApiClient_NonSuccess_ThrowsSafeException`
  - `tests/Integration/EclipseAi.Tests.Integration/ChatScenariosTests.cs` - `InforCalls_IncludeBearerAuth_AndReuseCachedToken`
- Confidence: high

## Contract-first endpoint shape for ERP scenarios
- What it does: Defines sample OpenAPI paths and DTO schemas for key ERP endpoints.
- Why it matters: Supports connector replacement and README credibility around API contract discipline.
- Evidence:
  - `contracts/eclipse.sample.openapi.json`
  - `tests/Contract/EclipseAi.Tests.Contract/OpenApiContractTests.cs`
  - `docs/adding-a-new-erp.md`
- Confidence: high

## Correlation propagation from request to ERP
- What it does: Accepts incoming `x-correlation-id` or generates one, propagates it through the request scope, outbound ERP calls, and audit reference naming.
- Why it matters: Strong traceability signal for supportability and integration debugging.
- Evidence:
  - `src/EclipseAi.Observability/Correlation.cs`
  - `apps/Gateway.Functions/Program.cs` - `/api/chat` reads header and passes to orchestrator
  - `apps/Gateway.Functions/Services/ChatOrchestrator.cs`
  - `src/EclipseAi.Connectors.Erp/ErpConnector.cs` - `TryAddCorrelationHeader`, `InforApiClient.AddHeadersAsync`
  - `tests/Integration/EclipseAi.Tests.Integration/ChatScenariosTests.cs` - `IncomingCorrelationId_IsPropagatedToResponseAndErp`
  - `tests/Unit/EclipseAi.Tests.Unit/UnitTest1.cs` - correlation header unit tests
- Confidence: high

## Unsupported / not evidenced integration patterns
- XML/SOAP handling: no evidence
- Event-driven ingestion or polling jobs: no evidence
- Database persistence: no evidence; audit and idempotency are file-based
- Multi-entity sync engine: no evidence
- Confidence: high

# 3. AI / ChatGPT capabilities

## Deterministic offline planner
- What it does: `FakePlanner` maps user text to tool calls using regex and simple rules.
- Where AI is used in the flow: This is the default non-OpenAI path; it replaces external AI for tests and demos.
- Why it matters: Makes the project demonstrable and testable without API keys or model variance.
- Evidence:
  - `src/EclipseAi.AI/FakePlanner.cs`
  - `src/EclipseAi.AI/PlannerFactory.cs`
  - `tests/Unit/EclipseAi.Tests.Unit/UnitTest1.cs` - `Plan_InventoryMessage_UsesInventoryTool`, `Plan_DraftMessage_UsesDeterministicRequestedDate`, `Plan_OrderExceptionMessage_UsesExceptionTool`
  - `tests/Integration/EclipseAi.Tests.Integration/ChatScenariosTests.cs` - `OpenAiModeOff_UsesDeterministicPlannerFlow`
- Confidence: high

## OpenAI tool/function calling planner
- What it does: Sends the user message and a tool schema to OpenAI Responses API, parses returned function calls, and converts them to internal `ToolCall` records.
- Where AI is used in the flow: Before ERP execution, to decide which tool should run and with which arguments.
- Why it matters: Shows practical LLM orchestration via server-side tool definitions rather than free-form text prompting only.
- Evidence:
  - `src/EclipseAi.AI/OpenAiPlanner.cs` - `HttpOpenAiClient.PlanToolsAsync`, `OpenAiToolSchema.Build`, `OpenAiResponseParser.ParseToolCalls`
  - `src/EclipseAi.AI/Planner.Abstractions.cs` - `IOpenAiClient`, `OpenAiPlannerSettings`
  - `src/EclipseAi.AI/PlannerFactory.cs`
  - `docs/real-openai-usage-flow.md`
  - `tests/Unit/EclipseAi.Tests.Unit/UnitTest1.cs` - `OpenAiPlanner_RealMode_UsesOpenAiClientToolCalls`
- Confidence: high

## Safe fallback from OpenAI to deterministic planning
- What it does: Falls back to `FakePlanner` when OpenAI mode is emulated, when no tool calls are returned, or when the OpenAI client throws.
- Where AI is used in the flow: Planner layer only; execution still passes through policy and handlers.
- Why it matters: Avoids hard runtime dependence on OpenAI for the main request path.
- Evidence:
  - `src/EclipseAi.AI/OpenAiPlanner.cs` - fallback branches in `Plan`
  - `src/EclipseAi.AI/PlannerFactory.cs` - mode gate `off|emulated|real`
  - `tests/Unit/EclipseAi.Tests.Unit/UnitTest1.cs` - `OpenAiPlanner_RealMode_FallsBackWhenClientFails`
  - `README.md`
- Confidence: high

## Optional OpenAI-backed order exception summarization
- What it does: For order exception scenarios, optionally sends governed context data to OpenAI for a one-sentence summary; falls back to deterministic wording on failure.
- Where AI is used in the flow: After ERP data retrieval and governance filtering, inside `ExplainOrderException`.
- Why it matters: This is the repo’s most mature example of AI being applied after data shaping rather than before controls.
- Evidence:
  - `src/EclipseAi.AI/OrderExceptionSummarizers.cs`
  - `src/EclipseAi.AI/OpenAiPlanner.cs` - `SummarizeOrderExceptionAsync`
  - `src/EclipseAi.AI/PlannerFactory.cs` - summarizer factory behavior
  - `apps/Gateway.Functions/Services/ChatToolHandlers.cs` - summarizer use in `ExplainOrderExceptionToolHandler`
  - `tests/Unit/EclipseAi.Tests.Unit/UnitTest1.cs` - summarizer factory and fallback tests
  - `tests/Integration/EclipseAi.Tests.Integration/ChatScenariosTests.cs` - `OpenAiSummarizeEnabled_UsesInjectedDeterministicSummary`
- Confidence: high

## Tool schema as explicit AI contract
- What it does: Defines server-owned tool names, descriptions, and JSON parameter schemas for inventory lookup, draft order creation, and exception explanation.
- Where AI is used in the flow: Tool schema is sent to OpenAI planner requests.
- Why it matters: Strong signal that the AI layer is constrained to explicit backend operations.
- Evidence:
  - `src/EclipseAi.AI/OpenAiPlanner.cs` - `OpenAiToolSchema.Build`
- Confidence: high

## OpenAI retry behavior and payload diagnostics
- What it does: Retries retryable OpenAI calls using `Retry-After` or exponential backoff with jitter; optional request/response payload logging is controlled by env vars.
- Where AI is used in the flow: Inside the OpenAI HTTP client wrapper.
- Why it matters: Shows real integration handling beyond a single happy-path HTTP call.
- Evidence:
  - `src/EclipseAi.AI/OpenAiPlanner.cs` - `SendResponsesRequestWithRetryAsync`, `ComputeRetryDelay`, `TryLogPayload`, `TryLogRetry`
  - `README.md`
  - `tests/Unit/EclipseAi.Tests.Unit/UnitTest1.cs` - payload logging tests
- Confidence: high

## AI capabilities not evidenced
- Embeddings/vector search: no evidence
- Semantic retrieval / RAG over documents: no evidence
- Classification or extraction pipelines beyond tool selection: no evidence
- Multi-turn memory/state store: no evidence
- Confidence: high

# 4. Important end-to-end flows

## Natural language inventory check -> ERP read -> evidence-backed response
- Short description: User message is planned into `GetInventoryAvailability`, validated, sent to ERP, then returned with structured evidence and an audit reference.
- Main components involved:
  - `apps/Gateway.Functions/Program.cs`
  - `apps/Gateway.Functions/Services/ChatOrchestrator.cs`
  - `src/EclipseAi.AI/FakePlanner.cs` or `src/EclipseAi.AI/OpenAiPlanner.cs`
  - `apps/Gateway.Functions/Services/ChatToolHandlers.cs` - `InventoryToolHandler`
  - `src/EclipseAi.Connectors.Erp/ErpConnector.cs`
  - `apps/Gateway.Functions/Services/AuditStore.cs`
- Evidence:
  - `tests/Integration/EclipseAi.Tests.Integration/ChatScenariosTests.cs` - `InventoryAvailabilityScenario_ReturnsContractAndEvidence`
  - `examples/requests.http`
- Mermaid candidate: yes

## Natural language draft order request -> guarded write -> idempotent replay-safe draft response
- Short description: Planner proposes `CreateDraftSalesOrder`; policy requires idempotency; handler validates payload; ERP draft write executes once; repeated request reuses stored draft result.
- Main components involved:
  - `src/EclipseAi.Governance/Governance.cs`
  - `apps/Gateway.Functions/Services/ChatToolHandlers.cs` - `DraftSalesOrderToolHandler`
  - `apps/Gateway.Functions/Services/AuditStore.cs` - `IdempotencyCache`
  - `src/EclipseAi.Connectors.Erp/ErpConnector.cs`
- Evidence:
  - `tests/Integration/EclipseAi.Tests.Integration/ChatScenariosTests.cs` - draft scenario and idempotency tests
  - `tests/Integration/EclipseAi.Tests.Integration/ChatOrchestratorTests.cs` - draft replay test
  - `DEMO.md`
- Mermaid candidate: yes

## Natural language order exception question -> ERP context retrieval -> governed evidence -> optional AI summary
- Short description: Planner proposes `ExplainOrderException`; ERP returns context; allowlist and redaction shape the data; optional summarizer produces one sentence; response returns evidence and audit ref.
- Main components involved:
  - `apps/Gateway.Functions/Services/ChatToolHandlers.cs` - `ExplainOrderExceptionToolHandler`
  - `src/EclipseAi.AI/OrderExceptionSummarizers.cs`
  - `src/EclipseAi.Governance/Governance.cs`
  - `src/EclipseAi.Connectors.Erp/ErpConnector.cs`
- Evidence:
  - `tests/Integration/EclipseAi.Tests.Integration/ChatScenariosTests.cs` - order exception tests and OpenAI summary test
  - `docs/real-openai-usage-flow.md`
- Mermaid candidate: yes

## Infor-shaped auth flow -> bearer token reuse -> ERP API calls
- Short description: In Infor mode, gateway acquires an OAuth2 client-credentials token, caches it, attaches bearer auth and correlation headers, then calls draft and exception endpoints.
- Main components involved:
  - `apps/Gateway.Functions/Program.cs`
  - `src/EclipseAi.Connectors.Erp/ErpConnector.cs` - `InforTokenClient`, `InforApiClient`, `InforErpConnector`
  - `tests/Integration/EclipseAi.Tests.Integration/ChatScenariosTests.cs` - `FakeInforServer`
- Evidence:
  - `tests/Integration/EclipseAi.Tests.Integration/ChatScenariosTests.cs` - `InforCalls_IncludeBearerAuth_AndReuseCachedToken`
  - `DEMO.md`
- Mermaid candidate: yes

# 5. Enterprise / integration credibility signals

## Draft-only write posture
- Why it matters: Lowers operational risk and shows deliberate separation between assistive drafting and committed ERP writes.
- Evidence:
  - `src/EclipseAi.Governance/Governance.cs`
  - `README.md`
  - `docs/decisions.md`
  - `docs/threat-model.md`
- Should this be highlighted in the README: yes

## Idempotency for write-like operations
- Why it matters: Important for retried requests, duplicate user asks, and safe middleware behavior around ERP writes.
- Evidence:
  - `apps/Gateway.Functions/Services/AuditStore.cs` - `IdempotencyCache`
  - `apps/Gateway.Functions/Services/ChatToolHandlers.cs` - draft handler
  - integration tests covering replay and conflict cases
- Should this be highlighted in the README: yes

## Governance before AI and audit
- Why it matters: The repo explicitly tries to constrain what data leaves the ERP boundary and what gets persisted.
- Evidence:
  - `apps/Gateway.Functions/Services/ChatToolHandlers.cs` - allowlisted exception fields
  - `src/EclipseAi.Governance/Governance.cs` - `MapRedactor`
  - `apps/Gateway.Functions/Services/ChatOrchestrator.cs` - redaction before audit write
  - `docs/decisions.md`
  - `docs/threat-model.md`
- Should this be highlighted in the README: yes

## Correlation-centric traceability
- Why it matters: Integration systems are hard to debug without cross-boundary identifiers.
- Evidence:
  - `src/EclipseAi.Observability/Correlation.cs`
  - `src/EclipseAi.Connectors.Erp/ErpConnector.cs`
  - integration and unit tests for correlation propagation
- Should this be highlighted in the README: yes

## Stable response contract
- Why it matters: Buyers and integrators care that the chat API returns predictable fields independent of planner/backend details.
- Evidence:
  - `src/EclipseAi.Domain/Models.cs` - `ChatResponse`
  - `apps/Gateway.Functions/Services/ChatOrchestrator.cs`
  - integration tests assert `correlationId`, `answer`, `toolCalls`, `evidence`, `auditRef`
- Should this be highlighted in the README: yes

## Contract-first connector posture
- Why it matters: OpenAPI artifact plus contract tests provide a migration path from mock endpoints to a real ERP adapter.
- Evidence:
  - `contracts/eclipse.sample.openapi.json`
  - `tests/Contract/EclipseAi.Tests.Contract/OpenApiContractTests.cs`
  - `docs/adding-a-new-erp.md`
- Should this be highlighted in the README: yes

## Safe auth and error handling in ERP client
- Why it matters: Secret leakage and opaque downstream failures are common integration weaknesses.
- Evidence:
  - `src/EclipseAi.Connectors.Erp/ErpConnector.cs` - `InforTokenClient`, `InforApiClient`, `InforApiException`
  - unit tests verify secret/token values are not echoed in exception messages
- Should this be highlighted in the README: yes

## Clear boundary between planner and executor
- Why it matters: The planner proposes actions; policy and handlers own execution. This reduces AI overreach.
- Evidence:
  - `apps/Gateway.Functions/Services/ChatOrchestrator.cs`
  - `docs/real-openai-usage-flow.md`
- Should this be highlighted in the README: yes

## File-based audit and idempotency persistence
- Why it matters: Provides a simple local-demo persistence model, but it is still explicit and testable.
- Evidence:
  - `apps/Gateway.Functions/Services/AuditStore.cs`
  - audit refs in response contract
- Should this be highlighted in the README: no

# 6. Engineering quality signals

## Small modular project split
- Evidence:
  - `src/EclipseAi.AI`
  - `src/EclipseAi.Connectors.Erp`
  - `src/EclipseAi.Domain`
  - `src/EclipseAi.Governance`
  - `src/EclipseAi.Observability`
  - `apps/Gateway.Functions`
- Why it matters for a hiring manager: Shows transport, AI, ERP, governance, and observability concerns were intentionally separated without excessive layering.

## Dependency injection with swappable planner and connector implementations
- Evidence:
  - `apps/Gateway.Functions/Program.cs`
  - interfaces in `src/EclipseAi.AI/Planner.Abstractions.cs` and `src/EclipseAi.Connectors.Erp/ErpConnector.cs`
  - test factories replacing services in integration tests
- Why it matters for a hiring manager: Signals testability and controlled extensibility rather than hard-coded infrastructure.

## Explicit DTOs and stable records
- Evidence:
  - `src/EclipseAi.Domain/Models.cs`
  - DTO records in `src/EclipseAi.Connectors.Erp/ErpConnector.cs`
- Why it matters for a hiring manager: Keeps boundaries typed and easier to reason about than ad hoc dictionaries at every layer.

## Thin host, centralized orchestration
- Evidence:
  - `apps/Gateway.Functions/Program.cs`
  - `apps/Gateway.Functions/Services/ChatOrchestrator.cs`
- Why it matters for a hiring manager: Demonstrates deliberate control flow and keeps the HTTP surface small.

## Deterministic tests with injected doubles
- Evidence:
  - `tests/Integration/EclipseAi.Tests.Integration/ChatScenariosTests.cs` - fake ERP, mutable planner, fake Infor server, deterministic OpenAI client
  - `src/EclipseAi.AI/FakePlanner.cs`
- Why it matters for a hiring manager: Shows the author optimized for repeatable integration tests rather than brittle live-service demos.

## Boundary-level HTTP tests
- Evidence:
  - `tests/Unit/EclipseAi.Tests.Unit/UnitTest1.cs` - connector header tests, token tests, API client tests, OpenAI logging tests
- Why it matters for a hiring manager: Verifies real transport concerns such as headers, auth, retries, and safe exceptions.

## Governance logic is test-covered
- Evidence:
  - unit tests for `ToolPolicy` and `MapRedactor`
  - integration tests for unknown tools, missing idempotency, invalid args, allowlisted evidence
- Why it matters for a hiring manager: Indicates the safety posture is not only documented but exercised.

## Practical observability design
- Evidence:
  - `src/EclipseAi.Observability/Correlation.cs`
  - startup and fallback logging in `apps/Gateway.Functions/Program.cs`
  - resilient logging wrapper in `apps/Gateway.Functions/Services/ChatOrchestrator.cs`
  - request logging in `mocks/Mock.Erp/Program.cs`
- Why it matters for a hiring manager: Mature integration work usually includes traceability and degraded-mode behavior, not only business logic.

## Contract verification as a separate test layer
- Evidence:
  - `tests/Contract/EclipseAi.Tests.Contract/OpenApiContractTests.cs`
- Why it matters for a hiring manager: Suggests the author thinks about external interface drift separately from implementation tests.

# 7. Proof assets

## Unit tests
- Status: present
- Evidence:
  - `tests/Unit/EclipseAi.Tests.Unit/UnitTest1.cs`
  - 29 `[Fact]` tests currently detected
- Useful for future README: yes

## Integration tests
- Status: present
- Evidence:
  - `tests/Integration/EclipseAi.Tests.Integration/ChatScenariosTests.cs`
  - `tests/Integration/EclipseAi.Tests.Integration/ChatOrchestratorTests.cs`
  - 18 `[Fact]` tests currently detected
- Useful for future README: yes

## Contract tests
- Status: present
- Evidence:
  - `tests/Contract/EclipseAi.Tests.Contract/OpenApiContractTests.cs`
  - 2 `[Fact]` tests currently detected
- Useful for future README: yes

## CI / GitHub workflow
- Status: absent
- Evidence:
  - `.github/workflows/` exists but contains no workflow files
- Useful for future README: no, but this is a gap

## Sample/demo app
- Status: present
- Evidence:
  - `apps/Gateway.Functions`
  - `mocks/Mock.Erp`
  - `DEMO.md`
  - `dev.ps1`
- Useful for future README: yes

## Usage examples
- Status: present
- Evidence:
  - `examples/requests.http`
  - `README.md`
  - `DEMO.md`
- Useful for future README: yes

## Screenshots / diagrams
- Status: partial
- Evidence:
  - `docs/diagrams/` contains PNG sequence and architecture-style diagrams
  - filenames indicate API/chat and demo sequences
- Useful for future README: yes

## Documentation
- Status: present
- Evidence:
  - `README.md`
  - `plan.md`
  - `docs/adding-a-new-erp.md`
  - `docs/decisions.md`
  - `docs/threat-model.md`
  - `docs/real-openai-usage-flow.md`
  - `docs/scenarios.md`
- Useful for future README: yes

## Coverage reports
- Status: absent
- Evidence:
  - no coverage report artifacts found in repo root, `tests/`, or CI config
- Useful for future README: low

## Example prompts
- Status: present
- Evidence:
  - `examples/requests.http`
  - demo commands in `DEMO.md`
  - tool-routing examples embedded in tests and `FakePlanner`
- Useful for future README: yes

## Sample ERP payloads / contract artifacts
- Status: present
- Evidence:
  - `contracts/eclipse.sample.openapi.json`
  - mock ERP responses in `mocks/Mock.Erp/Program.cs`
  - fake Infor server payloads in integration tests
- Useful for future README: yes

## Package/version metadata
- Status: partial
- Evidence:
  - project files specify `TargetFramework` and test package references
  - no NuGet-style package metadata such as `PackageId`, `Description`, or `Version` found in project files
- Useful for future README: limited

# 8. Safe README claims

1. Provides a stable `/api/chat` contract that returns `correlationId`, `answer`, `toolCalls`, `evidence`, and `auditRef`.
2. Supports three implemented ERP-facing scenarios: inventory lookup, draft sales order creation, and order exception explanation.
3. Runs offline by default with a deterministic planner, so the demo path does not require an OpenAI key.
4. Uses explicit tool allowlisting before any planned action is executed.
5. Requires an `idempotencyKey` for draft order creation and replays the same draft result on repeated requests.
6. Separates planning from execution: the planner proposes tool calls, while handlers and policy own ERP execution.
7. Includes an Infor-shaped ERP adapter path with OAuth2 client-credentials token acquisition and cached token reuse.
8. Propagates `x-correlation-id` from the inbound request to outbound ERP calls and audit records.
9. Filters order-exception evidence to an allowlisted subset before returning it.
10. Redacts sensitive field names before audit persistence.
11. Uses OpenAI tool/function calling in real mode and falls back to deterministic planning on failure.
12. Supports optional OpenAI-backed order-exception summarization with deterministic fallback behavior.
13. Verifies connector and contract behavior with unit, integration, and contract tests.
14. Ships with a mock ERP service and local demo scripts for end-to-end testing.
15. Uses an OpenAPI contract artifact to describe the ERP endpoint shapes the kit depends on.

# 9. Mermaid candidates

## Candidate 1
- Diagram type: sequence diagram
- What it would show: `/api/chat` end-to-end request flow from user message through planner, policy, handler, ERP connector, governance, audit, and response.
- Why it helps: Best single diagram for both technical evaluators and buyers because it explains the full controlled execution path.
- Rough nodes/steps to include:
  - Client
  - Gateway `/api/chat`
  - `ChatOrchestrator`
  - `IAiPlanner` (`FakePlanner` or `OpenAiPlanner`)
  - `ToolPolicy`
  - `IChatToolHandler`
  - `IErpConnector`
  - `IRedactor`
  - `IAuditStore`
  - Response contract fields

## Candidate 2
- Diagram type: component diagram / flowchart
- What it would show: backend module boundaries and dependencies across `apps/`, `src/`, `mocks/`, `contracts/`, and `tests/`.
- Why it helps: Good for hiring-manager audience because it shows modularity and where AI, governance, ERP, and observability responsibilities live.
- Rough nodes/steps to include:
  - Gateway host
  - AI module
  - Governance module
  - ERP connector module
  - Domain contracts
  - Observability
  - Mock ERP
  - OpenAI Responses API
  - OpenAPI contract artifact

## Candidate 3
- Diagram type: sequence diagram
- What it would show: guarded draft-order flow with idempotency reservation, ERP write, completion, and replay behavior on duplicate requests.
- Why it helps: This is the most differentiated backend/integration flow in the repo.
- Rough nodes/steps to include:
  - Client
  - Planner
  - `ToolPolicy`
  - `DraftSalesOrderToolHandler`
  - `IdempotencyCache`
  - `IErpConnector` / Infor endpoint `/orders/draft`
  - Audit store
  - Duplicate request branch returning existing draft

# 10. Missing gaps

- Missing actual GitHub Actions workflow.
  - Evidence: `.github/workflows/` is empty.
- Missing a short architecture note that reconciles the current minimal API host with the stated Azure Functions target.
  - Evidence: `README.md` says the current host is ASP.NET Core minimal API while Azure Functions remains the primary target; `local.settings.example.json` and `host.json` still exist.
- Missing explicit README-ready screenshots or exported diagram markdown.
  - Evidence: `docs/diagrams/` contains PNG assets, but there is no lightweight architecture diagram source in Markdown/Mermaid.
- Missing a concise limitations / non-goals section.
  - Evidence: docs mention on-hold items, but there is no single consolidated public limitations section.
- Missing a sample real-mode configuration guide for Infor credentials beyond env variable names.
  - Evidence: `Program.cs` wires `INFOR_*` settings, but there is no dedicated setup doc for a real ERP environment.
- Missing coverage reporting or summarized test matrix output.
  - Evidence: tests exist, but no coverage artifact or badge source is present.
- Missing example audit file and idempotency file snippets in docs.
  - Evidence: file-based persistence exists in code, but no sample artifact is documented.
- Missing a clearer explanation of the contract-first migration path from mock endpoints to real Eclipse APIs.
  - Evidence: `docs/adding-a-new-erp.md` exists, but it is brief.
- Missing evidence of committed-write flows, async/event-driven patterns, or richer ERP synchronization.
  - Evidence: only draft-only write and read/query flows are implemented.
- Missing a concrete explanation of why the repo now references Infor-shaped endpoints while the narrative still says Epicor Eclipse.
  - Evidence: `README.md`, `plan.md`, and code mention both Eclipse and Infor-shaped flows.
