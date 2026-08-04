<!--
  DRAFT — not an approved specification.

  This document collects what is known and what is undecided about a module that has not been
  built. It is a starting point for the scoping conversation, not a commitment: the Golden Rule
  (GymOS-Specification/PRODUCT-PRINCIPLES.md) is the scope authority, and every rule below must be
  confirmed with the product owner before it is implemented. Rules marked (?) are assumptions.
-->

# Attendance — Business Rules

**Module:** `GymOS.Modules.Attendance` · schema `attendance` · tenant-scoped
**Status:** not started.

## Purpose

Who came in, when, and how they got through the door. Feeds retention analytics, entitlement
consumption, and the gym's own capacity picture.

It is also the module most exposed to **hardware that is offline, wrong, or lying about the time** —
which is why the gateway design (AD-21) and the clock-skew rules (AD-41) matter here more than
anywhere else.

## Entities

| Entity | Notes |
|---|---|
| `CheckIn` | One entry: member, timestamp, method, device, branch. |
| `CheckOut` | Optional; many gyms never capture it. |
| `AttendanceMethod` | QR, card, fob, PIN, manual by staff, face (out of scope pending assessment). |
| `OccupancySnapshot` | Derived count per branch over time, for the capacity view. |
| `VisitConsumption` | Links a check-in to the entitlement it consumed. |

## Business Rules

| Code | Rule |
|---|---|
| `attendance.requires_active_membership` | Or an explicit staff override, which is audited as its own action. |
| `attendance.duplicate_within_window_ignored` | A second scan within a short window is the same visit, not two. Turnstiles double-fire. |
| `attendance.idempotent_by_local_sequence` | Gateway-originated check-ins carry a `local_sequence`; replay after reconnect must not create a second visit (AD-39). |
| `attendance.gateway_time_annotated_not_rewritten` | An appliance's clock is untrusted: store what it reported, plus measured skew and `received_at`. Business questions use skew-adjusted time; raw values are never silently corrected (AD-41). |
| `attendance.offline_check_in_accepted` | The gateway decides locally when the cloud is unreachable, and syncs later. Offline-first is the design, not a fallback. |
| `attendance.manual_check_in_requires_permission` | And records who did it. |
| `attendance.no_check_in_before_membership_start` | |
| `attendance.capacity_warning_not_block` | Exceeding a soft capacity warns staff; blocking a paying member at the door needs an explicit decision (?). |

## Invariants

- A check-in is immutable once recorded. A mistake is corrected by a compensating record, not an edit —
  attendance feeds billing-adjacent entitlement consumption.
- Every check-in resolves to exactly one member, or is explicitly an anonymous/guest entry.
- Occupancy is derived from check-ins and check-outs, never stored as a live counter that a restart
  could lose.
- A check-in that consumed an entitlement always references it.

## Future Rules

- Class attendance versus gym attendance, and whether one implies the other.
- Guest passes and day passes.
- Access zones (pool, sauna) with separate entitlements.
- Anti-passback: the same credential entering twice without leaving.
- Retention: how long is individual visit history kept versus aggregated?

## Dependencies

- **Members**, **Memberships** (entitlement), **Scheduling** (class attendance), **Gateway** (AD-21
  devices and HAL), platform **time service**, **idempotency**, **jobs** (occupancy rollups).
- **Events:** `gymos.attendance.member.checked_in`, `.checked_out`, `.check_in_denied`.

## Open Questions

1. Does exceeding capacity block entry? A member turned away is a support call; an overfull gym is a
   safety issue. Likely a per-gym setting.
2. Is check-out captured at all in the MVP? Without it, occupancy is an estimate.
3. **Biometric methods** (face, fingerprint) are GDPR Art. 9 data and need an explicit basis and a DPIA
   before any design. **Verify with legal counsel** — recommend out of scope until then.
4. Individual visit-history retention period, and does the analytics rollup survive erasure of the
   underlying rows?
5. How is a stolen or shared credential detected and handled?

---

## How this document is used

Before the module is implemented: walk the Business Rules against the Golden Rule and cut what does
not reduce work for the gym owner. Then turn the survivors into `IBusinessRule` implementations
(domain, pure) or Application-layer guards (cross-aggregate), with the codes below as their `Code`
values. Answer the Open Questions first — every one of them changes the shape of the entities.
