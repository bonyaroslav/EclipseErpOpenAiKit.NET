# EclipseErpOpenAiKit.NET modernization plan

## Goal

Maintain a production-minded reference implementation of governed ERP tool orchestration on .NET 10. The model proposes; .NET policy and handlers govern; the ERP connector executes; correlated evidence and audit records prove what happened.

The current runtime is the ASP.NET Core Minimal API in `apps/Gateway.Functions`. The directory name and leftover Functions configuration files are historical; Azure Functions is not the active host.

## Stable demo contract

A new user should be able to exercise a deterministic local path in which:

- `POST /api/chat` triggers at least one mock ERP connector call for an eligible request.
- Every response retains `correlationId`, `answer`, `toolCalls`, `evidence`, and `auditRef`.
- Inventory availability, draft sales order, and order exception scenarios remain available.
- Draft creation remains draft-only and idempotent.
- No OpenAI key is required because the offline path uses `FakePlanner`.
- Optional OpenAI and Infor-shaped paths remain configuration-gated.
- The existing `dev.ps1 up|run|demo|demo-infor|test` command interface remains unchanged.

## Focused modernization scope

1. **Truthful baseline:** align product, runtime, capability, and limitation documentation with the code and automated evidence.
2. **Real OpenAI hardening:** use strict schemas, disable response storage explicitly, remove sync-over-async, and propagate cancellation without converting cancellation to fallback.
3. **Real-ERP write closure:** prevent planner failures from inferring writes and require trusted approval for real-ERP drafts while keeping the mock demo convenient.
4. **Verified customer context:** make request-scoped server or ERP state authoritative for draft customer identity and audit proposed versus effective arguments.
5. **Workflow proof:** add a minimal CI and container smoke path for the complete offline workflow.

The local tickets under `.scratch/governed-erp-modernization/issues/` define the detailed acceptance criteria and dependency order.

## Rejected platform scope

This modernization does not add or migrate to:

- Azure Functions or another host platform
- autonomous or multi-agent execution
- MCP-native transport
- committed live ERP writes
- conversation memory or durable session lifecycle
- event-driven ingestion, synchronization, RAG, or vector search
- cloud IaC, deployment environments, or a secrets platform
- generic ERP repositories, SDK packaging, or broad connector coverage

The bounded tool-result loop is also **deferred**. The current orchestrator performs one planning pass and executes only eligible proposals. A second model turn over governed tool results requires a separate design and is not an implemented capability.

## Implemented baseline

- ASP.NET Core Minimal API endpoints for `POST /api/chat` and `GET /health`.
- Stable chat response contract with correlation, executed tool calls, evidence, and audit reference.
- Deterministic `FakePlanner` selected when OpenAI is unavailable, disabled, or emulated.
- Optional OpenAI Responses API planning and order-exception summarization.
- Strict, non-stored OpenAI requests with asynchronous planning, summarization, retries, and request cancellation.
- Tool allowlist and handler-level argument validation.
- Draft-only creation guarded by idempotency reservation, replay, and payload-conflict checks.
- Evidence allowlisting and recursive sensitive-field redaction before audit persistence.
- Correlation propagation from inbound request through ERP HTTP calls and audit output.
- Default mock ERP connector and an Infor-shaped OAuth2/typed HTTP connector.
- Unit, integration, and OpenAPI contract tests that run without OpenAI.

## Partial capabilities to harden

- Real OpenAI failure currently falls back to the deterministic planner, which can propose a draft write.
- Real ERP mode has no trusted approval boundary for drafts.
- Model-proposed customer identity is not yet replaced with verified request-scoped customer context.
- Audit and idempotency persistence are local file-based examples.
- The `dev.ps1 run` launch profile does not currently use the demo's port, and CI plus a complete container smoke workflow are not yet present.

## Delivery risks

| Risk | Required treatment |
|---|---|
| A real-model failure or empty plan becomes a write through fallback | Restrict real-mode fallback to permitted reads and prove failure closure |
| Cancellation is swallowed or converted to fallback | Propagate request cancellation through planning, retry delay, and HTTP calls |
| A model redirects a draft to an unverified customer | Resolve identity from trusted request-scoped state and execute only effective arguments |
| Documentation outruns implementation | Tie public claims to code, tests, contract artifacts, or the demo |
| Local-only success hides clean-checkout failures | Add offline CI and container smoke proof after governance hardening |
| Legacy host names and artifacts imply an inactive runtime | State the ASP.NET Core runtime explicitly; remove obsolete infrastructure only in the workflow-proof ticket |

## Acceptance checks

- `dotnet test EclipseErpOpenAiKit.NET.sln -p:RestoreUseStaticGraphEvaluation=true` passes without calling OpenAI.
- `/api/chat` preserves the existing request and response shapes.
- Unknown tools and invalid arguments do not reach ERP execution.
- Draft creation requires idempotency; same-payload replay calls downstream once; conflicting payload reuse is blocked.
- Order-exception evidence contains only allowlisted fields, and audit payloads apply redaction.
- Incoming correlation IDs propagate to outbound ERP calls and audit records.
- The default mock/demo path remains deterministic and requires no credentials.
- Public docs identify implemented, partial, deferred, and out-of-scope capabilities separately.
- No public claim presents the project as an Azure Functions runtime, an autonomous agent platform, or a deployment-ready ERP product.

## Evidence

- Host and dependency wiring: `apps/Gateway.Functions/Program.cs`
- Stable contracts: `src/EclipseAi.Domain/Models.cs`
- Orchestration and audit payload: `apps/Gateway.Functions/Services/ChatOrchestrator.cs`
- Governance: `src/EclipseAi.Governance/Governance.cs`
- ERP clients: `src/EclipseAi.Connectors.Erp/ErpConnector.cs`
- Offline planner: `src/EclipseAi.AI/FakePlanner.cs`
- Unit proof: `tests/Unit/EclipseAi.Tests.Unit/UnitTest1.cs`
- Scenario and governance proof: `tests/Integration/EclipseAi.Tests.Integration/ChatScenariosTests.cs`
- Contract proof: `tests/Contract/EclipseAi.Tests.Contract/OpenApiContractTests.cs`
- Local commands: `dev.ps1`, `DEMO.md`, and `examples/requests.http`
