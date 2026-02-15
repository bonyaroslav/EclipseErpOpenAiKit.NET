# Threat model (small, practical)

- Prompt injection: only registered tools, with server-side argument validation.
- Data exfiltration: field allowlists and redaction before AI calls and audit persistence.
- Unsafe writes: draft-only default; commit disabled unless explicitly configured.
- Secrets: environment variables or user-secrets only; no secrets in repo.
- Auditability: correlation ID across request, tool call, ERP call, and audit record.
