# Real OpenAI Usage Flow

This document explains, in plain language, what happens when the gateway runs with real OpenAI enabled.

## When Real OpenAI Is Used

Real OpenAI calls happen only when:

- `OPENAI_API_KEY` is set
- `OPENAI_MODE=real`

There are two OpenAI call scenarios:

1. Planner call: decide which tool to execute for the user message.
2. Optional summarization call: write a one-sentence summary for order exceptions (only if `OPENAI_SUMMARIZE=1`).

## End-to-End Flow (Human Language)

1. User sends a message to `/api/chat`.
2. The orchestrator asks the planner: "What should I do?"
3. The planner sends message + tool definitions to OpenAI.
4. OpenAI returns a function call (tool name + arguments).
5. Policy checks if the tool call is allowed and safe.
6. Matching tool handler executes the ERP action.
7. For order exceptions, optional summarizer may call OpenAI again for a one-line summary.
8. Orchestrator returns stable response contract and writes redacted audit payload.

## Component Responsibilities

### ChatOrchestrator

- Coordinates the full request lifecycle.
- Calls planner, runs policy checks, executes handlers, builds response, writes audit.
- File: `apps/Gateway.Functions/Services/ChatOrchestrator.cs`

### Planner (OpenAiPlanner + Fake fallback)

- Decides which tool call(s) to produce from user text.
- In real mode, calls OpenAI `/v1/responses` with tool schema.
- Falls back to deterministic planner if OpenAI fails or returns no tool calls.
- Files:
  - `src/EclipseAi.AI/OpenAiPlanner.cs`
  - `src/EclipseAi.AI/PlannerFactory.cs`
  - `src/EclipseAi.AI/FakePlanner.cs`

### ToolPolicy

- Enforces safety rules before execution.
- Allows only allowlisted tools.
- Requires `idempotencyKey` for draft-write tool.
- File: `src/EclipseAi.Governance/Governance.cs`

### Tool Handlers

- Execute ERP operations for each tool.
- Parse and validate arguments.
- Return answer text + evidence fields.
- File: `apps/Gateway.Functions/Services/ChatToolHandlers.cs`

### ERP Connector

- Performs actual ERP HTTP operations.
- Propagates correlation id to outbound calls.
- File: `src/EclipseAi.Connectors.Erp/ErpConnector.cs`

### Order Exception Summarizer (optional OpenAI call)

- Used only in `ExplainOrderException` flow.
- In real mode with summarize enabled, calls OpenAI for one sentence.
- Falls back to deterministic sentence on failure.
- File: `src/EclipseAi.AI/OrderExceptionSummarizers.cs`

## What the Planner Does Not Do

- It does not execute ERP calls.
- It does not bypass policy.
- It does not write audit records.

It only proposes tool calls. The orchestrator and handlers do the execution.

## Real-Mode Verification Checklist

1. Set env vars:

```powershell
$env:OPENAI_API_KEY = "sk-..."
$env:OPENAI_MODE = "real"
$env:OPENAI_SUMMARIZE = "1"   # optional
$env:OPENAI_LOG_PAYLOADS = "1"
```

2. Start app and verify startup log includes:

- `openai_mode=real`
- `openai_key_present=True`

3. Send chat request and verify logs show planner OpenAI call:

- `openai_request endpoint=responses operation=plan_tools`
- `openai_response ... type=function_call`

4. For order exception scenario, verify optional summarize call:

- `openai_request endpoint=responses operation=summarize_order_exception`
- `openai_response ... type=message`

If planner OpenAI fails, expected behavior is deterministic fallback (request still completes).
