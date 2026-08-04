<!--
  DRAFT — not an approved specification.

  This document collects what is known and what is undecided about a module that has not been
  built. It is a starting point for the scoping conversation, not a commitment: the Golden Rule
  (GymOS-Specification/PRODUCT-PRINCIPLES.md) is the scope authority, and every rule below must be
  confirmed with the product owner before it is implemented. Rules marked (?) are assumptions.
-->

> **Superseded for this domain.** The authoritative business specification is
> [`docs/Domain/MEMBERS-DOMAIN-DESIGN.md`](../Domain/MEMBERS-DOMAIN-DESIGN.md). That document decides
> what this draft only listed, and carries the Open Product Decisions that block implementation.
> This file is kept for the questions it raised; where the two disagree, the domain design wins.

# Memberships — Business Rules

**Module:** `GymOS.Modules.Memberships` · schema `memberships` · tenant-scoped
**Status:** not started.

## Purpose

What a member has bought and what it entitles them to. Every access decision at the door, every
booking allowance, and every recurring charge resolves through here — which makes this the module
whose rules are most often *asked* about and hardest to change later.

## Entities

| Entity | Notes |
|---|---|
| `MembershipPlan` | What a gym sells: duration, price, entitlements. Tenant-scoped, distinct from the platform's `subscription_plans` (what the gym itself buys). |
| `Membership` | One member's holding of a plan. Aggregate root. |
| `MembershipPeriod` | A billing/entitlement window. History is preserved; a renewal adds a period. |
| `MembershipFreeze` | A suspension with a reason and a date range. |
| `Entitlement` | What the plan grants: visits per period, class credits, branch access, guest passes. |

## Business Rules

| Code | Rule |
|---|---|
| `memberships.one_active_per_member` | One active membership per member per plan family (?). Overlapping memberships make entitlement arithmetic ambiguous. |
| `memberships.start_not_before_member_registration` | |
| `memberships.end_after_start` | |
| `memberships.freeze_within_period` | A freeze cannot extend beyond the period it freezes. |
| `memberships.freeze_extends_end_date` | A frozen membership's end date moves by the frozen duration — the member paid for time, not for a calendar range (?). |
| `memberships.freeze_limit_per_period` | A plan may cap freezes (count and total days), or freezing becomes an indefinite free hold. |
| `memberships.cannot_cancel_retroactively` | Cancellation is effective now or at period end, never backdated — backdating would require reversing charges already settled. |
| `memberships.entitlement_not_negative` | Consuming a visit or credit below zero is refused, not clamped. |
| `memberships.expired_grants_no_access` | Access is derived from the period, never from a status column someone must remember to update. |
| `memberships.plan_price_snapshot` | A membership records the price it was sold at. Changing a plan's price never re-prices an existing membership. |

## Invariants

- Entitlement is **computed** from periods, freezes, and consumption — never a stored counter that a
  job has to keep true. A stored balance and a derived one will disagree, and the disagreement will be
  discovered by a member at the door.
- A membership's periods never overlap.
- A membership cannot exist without a member and a plan.
- Cancelling never deletes history: it ends a period and records why.

## Future Rules

- Proration on mid-period upgrade or downgrade.
- Trial memberships, and what happens when a trial lapses without payment.
- Corporate and family memberships with a payer who is not the member.
- Loyalty and referral credits.
- Automatic renewal, and its interaction with a failed payment (dunning).

## Dependencies

- **Members** (the subject), **Payments** (charges and dunning), **Attendance** (consumption),
  **Scheduling** (booking entitlement), platform **feature flags**.
- **Events:** `gymos.memberships.membership.started`, `.renewed`, `.frozen`, `.unfrozen`,
  `.cancelled`, `.expired`, `gymos.memberships.entitlement.consumed`.

## Open Questions

1. Does a freeze extend the end date or forfeit the time? Materially changes revenue and member
   expectation, and gyms differ — is it per plan?
2. Is "one active membership" a hard rule, or do add-ons (personal training, pool access) count as
   separate memberships?
3. Who may override an entitlement at the desk, and is an override audited as its own action?
4. Does an unpaid membership grant access during a grace period, and how long?
5. Do entitlements reset on a calendar boundary or on the membership's own anniversary?

---

## How this document is used

Before the module is implemented: walk the Business Rules against the Golden Rule and cut what does
not reduce work for the gym owner. Then turn the survivors into `IBusinessRule` implementations
(domain, pure) or Application-layer guards (cross-aggregate), with the codes below as their `Code`
values. Answer the Open Questions first — every one of them changes the shape of the entities.
