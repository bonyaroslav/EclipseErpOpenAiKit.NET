# Threat model (small, practical)

- Prompt injection: only registered tools; validate tool args server-side.
- Data exfiltration: field allowlists + redaction before any AI call.
- Unsafe writes: draft-only default; commit disabled without explicit configuration + approval provider.
- Secrets: env vars / user-secrets only; no secrets in repo.
- Auditability: correlationId everywhere + audit events per ERP/tool call.
