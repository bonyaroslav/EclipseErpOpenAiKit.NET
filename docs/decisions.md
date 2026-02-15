# Decisions

## Offline-first default

The default planner is deterministic (`FakePlanner`) so local demo and tests are stable and do not require external services.

## Draft-only writes

Write actions remain draft-only by default to reduce operational risk during demo and early integration.

## Contract-first connector

Mock ERP behavior is defined by OpenAPI artifacts under `contracts/`, creating a clear migration path to real Eclipse APIs.

## Governance before AI and audit

Field allowlisting and redaction occur before evidence is returned and before audit persistence.

## Correlation-centric observability

Every request gets a correlation ID propagated through tool execution, ERP calls, and audit records.
