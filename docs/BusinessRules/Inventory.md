<!--
  DRAFT — not an approved specification.

  This document collects what is known and what is undecided about a module that has not been
  built. It is a starting point for the scoping conversation, not a commitment: the Golden Rule
  (GymOS-Specification/PRODUCT-PRINCIPLES.md) is the scope authority, and every rule below must be
  confirmed with the product owner before it is implemented. Rules marked (?) are assumptions.
-->

# Inventory — Business Rules

**Module:** `GymOS.Modules.Inventory` · schema `inventory` · tenant-scoped
**Status:** not started.

## Purpose

Stock the gym holds and sells: drinks, supplements, merchandise, and consumables. Exists mainly to
serve POS and to stop the gym selling what it does not have.

## Entities

| Entity | Notes |
|---|---|
| `Product` | What is sold. SKU, name, category, tax class. |
| `ProductVariant` | Size, flavour, colour. Stock is held per variant, not per product. |
| `StockLevel` | On-hand quantity per variant per location. |
| `StockMovement` | Every change, with a reason. Append-only — the ledger stock is derived from. |
| `Supplier`, `PurchaseOrder` | Restocking. |
| `StockCount` | A physical count and the variance it revealed. |

## Business Rules

| Code | Rule |
|---|---|
| `inventory.stock_from_movements` | On-hand is derived from movements, not a column someone updates. A stored quantity and its ledger will disagree, and the count will be blamed. |
| `inventory.movement_requires_reason` | Sale, restock, wastage, correction, transfer. A movement with no reason is an unexplained loss. |
| `inventory.no_negative_stock` | Selling below zero is refused by default; a per-product setting may allow it for items that are ordered on demand (?). |
| `inventory.sku_unique_per_tenant` | |
| `inventory.price_change_not_retroactive` | A sale records the price charged; changing a product's price never rewrites history. |
| `inventory.expiry_tracked_where_applicable` | Supplements and food have expiry dates; selling expired stock is a safety and legal problem, not an inconvenience. |
| `inventory.count_variance_audited` | A stock count that disagrees with the ledger produces an audited correction, never a silent overwrite. |
| `inventory.transfer_conserves_quantity` | A transfer between locations is two movements that sum to zero. |

## Invariants

- Stock is per (variant, location); a product with no variant still has exactly one.
- Movements are immutable and append-only.
- A sale's movement references the sale, so any stock change can be traced to its cause.
- Deactivating a product never deletes its history or its movements.

## Future Rules

- Reorder points and automatic purchase suggestions.
- Batch and lot tracking for recalls.
- Supplier price lists and cost-of-goods reporting.
- Stock across branches with a shared catalogue.
- Barcode scanning at the till.

## Dependencies

- **POS** (the consumer), **Payments** (via POS), platform **Money**, **file pipeline** (product
  images), **jobs** (expiry alerts, reorder checks).
- **Events:** `gymos.inventory.stock.moved`, `gymos.inventory.stock.depleted`,
  `gymos.inventory.product.created`, `.deactivated`.

## Open Questions

1. Is inventory in the MVP at all, or does POS start with services only? The Golden Rule test: does
   stock tracking reduce the owner's work, or add a task they will skip?
2. Are negative stock levels ever allowed, and for which products?
3. Multi-location from the start, or single-location with a migration later? Retrofitting a location
   dimension onto stock is a data migration.
4. Cost tracking (FIFO, average) — needed for reporting, or out of scope?
5. Cyprus rules on expiry-dated supplement sales — **verify before implementing expiry handling**.

---

## How this document is used

Before the module is implemented: walk the Business Rules against the Golden Rule and cut what does
not reduce work for the gym owner. Then turn the survivors into `IBusinessRule` implementations
(domain, pure) or Application-layer guards (cross-aggregate), with the codes below as their `Code`
values. Answer the Open Questions first — every one of them changes the shape of the entities.
