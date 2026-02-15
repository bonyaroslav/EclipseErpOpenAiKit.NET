# Scenarios (spec)

## 1) Inventory availability

Input: "Do we have ITEM-123 in warehouse MAD? What's available and ETA?"
Tool: `GetInventoryAvailability(itemId, warehouseId)`
ERP: `GET /inventory/{itemId}?warehouse={warehouseId}`

## 2) Draft sales order (guarded)

Input: "Create a draft order for ACME: 10x ITEM-123, ship tomorrow."
Tool: `CreateDraftSalesOrder(customerId, lines, shipDate, idempotencyKey)`
ERP: `POST /draftSalesOrders`
Rules: allowlist + idempotency required + draft-only

## 3) Order exception copilot (WOW)

Input: "Why is SO-456 delayed and what should I do?"
ERP calls: `/salesOrders/{id}`, `/holds`, `/lines`, `/inventory`, `/customers/{id}/arSummary`
Output: summary + reasons (with evidence) + next actions + optional draft customer message

Current MVP implementation note:
- The running MVP currently uses a consolidated endpoint (`GET /orderException/{orderId}`) for this scenario.
- Multi-endpoint expansion remains on hold.
