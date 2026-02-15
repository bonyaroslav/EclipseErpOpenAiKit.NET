# AGENTS.md

## Purpose
Help build **EclipseErpOpenAiKit.NET**: a minimal, production-shaped **Epicor Eclipse ERP ↔ OpenAI** integration kit, hosted primarily on **Azure Functions (.NET 10 isolated)**.

## Source of truth
- **README.md** defines product narrative and demo expectations.
- **plan.md** defines scenarios, response contract, commands, and acceptance criteria.
If AGENTS conflicts with README/plan, **README/plan win**.

## Non-negotiables (agent constraints)
- Keep scope minimal: implement only what is required to satisfy **plan.md acceptance criteria**.
- **Offline-first:** tests must not call OpenAI. Use a deterministic `FakePlanner` for tests.
- OpenAI integration exists via `OpenAiPlanner` (tool/function calling + optional summarization), gated by config/env.
- **Safety posture:** draft-only writes by default; enforce tool allowlist + idempotency for draft-write tools.
- **Governance:** field allowlists + redaction before any AI call and before audit persistence.
- **Auditability/observability:** correlation ID propagated across request → tool call → ERP call → audit record; structured logs.

## Working rules (anti-garbage)
- Prefer editing existing files over creating new ones.
- Avoid over-architecture: no deep layering, no generic repositories, no “god orchestrators”.
- Keep public surface small: minimal DTOs/endpoints.
- If you introduce an interface, include the single concrete implementation and usage in the same change.
- No new production dependencies unless they directly support the plan’s E2E/demo/test goals.

## TDD workflow
- Write/adjust tests first when feasible (red → green → refactor).
- Implement only what tests require; avoid speculative features.
- Always run `dotnet test` before finishing a task.

## Planning discipline
For changes that touch >2 modules or are likely >1 day:
- Update **plan.md** (root) with goal/non-goals/steps/risks/acceptance checks.
- Implement strictly to the updated plan.

## Definition of Done (any change)
- `dotnet test` passes
- Demo + E2E path in **plan.md** still works (do not break the command interface)
- No unused abstractions
- README/plan updated if public behavior changes
