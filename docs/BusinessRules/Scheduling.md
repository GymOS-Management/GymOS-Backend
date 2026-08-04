<!--
  DRAFT — not an approved specification.

  This document collects what is known and what is undecided about a module that has not been
  built. It is a starting point for the scoping conversation, not a commitment: the Golden Rule
  (GymOS-Specification/PRODUCT-PRINCIPLES.md) is the scope authority, and every rule below must be
  confirmed with the product owner before it is implemented. Rules marked (?) are assumptions.
-->

# Scheduling — Business Rules

**Module:** `GymOS.Modules.Scheduling` · schema `scheduling` · tenant-scoped
**Status:** not started, but **already designed**: ARCHITECTURE-PLATFORM-CAPABILITIES.md §1 (AD-25)
is approved. This document collects the rules, not the architecture.

## Purpose

Classes, their occurrences, and who is booked into them. The correctness core of the gym-facing
product: a double-booked class or a lost booking is visible to members immediately.

## Entities

Per the approved design — templates versus materialized occurrences:

| Entity | Notes |
|---|---|
| `ScheduleTemplate` | The recurring intent: "Yoga, Mondays 18:00, studio 2, capacity 20". |
| `ClassOccurrence` | A materialized instance on a date, with its own capacity and instructor. Cancelling one occurrence does not touch the template. |
| `Booking` | A member's place in an occurrence. |
| `WaitlistEntry` | Ordered, with an offer window when a place frees. |
| `Resource` | Room, studio, or equipment an occurrence consumes. |

## Business Rules

| Code | Rule |
|---|---|
| `scheduling.occurrence_within_opening_hours` | (?) |
| `scheduling.no_resource_double_booking` | One room cannot host two occurrences at once. The conflict check is the module's core correctness requirement. |
| `scheduling.no_instructor_double_booking` | Same, for people. |
| `scheduling.capacity_not_exceeded` | Enforced under concurrency: two members booking the last place must not both succeed. Needs a transactional check, not a read-then-write. |
| `scheduling.booking_requires_entitlement` | A booking consumes a Memberships entitlement, checked at booking time. |
| `scheduling.cancellation_window` | Cancelling inside the window forfeits the entitlement (?), which is what makes no-shows costly. |
| `scheduling.no_double_booking_same_member` | A member cannot hold two overlapping bookings. |
| `scheduling.waitlist_offer_expires` | An offer to a waitlisted member expires and passes to the next. |
| `scheduling.wall_clock_scheduling` | "Mon 18:00" means 18:00 on the gym's wall clock all year; occurrences are computed in the tenant zone then converted per occurrence to UTC (AD-41). |
| `scheduling.dst_policy_explicit` | Spring-forward skipped times move forward; fall-back ambiguous times take the first occurrence (AD-41). Decided centrally, never per module. |
| `scheduling.past_occurrence_immutable` | A finished occurrence is not editable; corrections are recorded, not overwritten. |

## Invariants

- An occurrence's booked count is **derived** from its bookings, never a stored counter — a counter
  and reality will diverge exactly when the class is full.
- Every occurrence stores a UTC instant *and* the tenant zone it was computed in.
- A booking references exactly one occurrence and one member.
- A cancelled occurrence cancels its bookings and notifies, in one transaction plus events.

## Future Rules

- Recurring bookings ("every Monday").
- Instructor substitution, and whether members are told.
- Paid drop-ins for non-members.
- Room layout and numbered spots.
- Public timetable (approved: §1.7) and its caching.

## Dependencies

- **Members**, **Memberships** (entitlement), **Attendance** (did they show up), **Trainer**
  (instructors), platform **time service** (`ITimeZoneService`, DST policy), **jobs**
  (materialization, waitlist expiry), **notifications**.
- **Events:** `gymos.scheduling.occurrence.created`, `.cancelled`, `gymos.scheduling.booking.made`,
  `.cancelled`, `gymos.scheduling.waitlist.offered`, `.expired`.

## Open Questions

1. How far ahead are occurrences materialized, and what happens when a template changes after
   materialization — do existing occurrences move?
2. Does cancelling inside the window forfeit the entitlement, charge a fee, or neither? Per plan?
3. Waitlist offer window length, and does it differ by how close the class is?
4. Can a member book for someone else (a guest, a child)?
5. Is capacity per occurrence, per resource, or the lower of the two?

---

## How this document is used

Before the module is implemented: walk the Business Rules against the Golden Rule and cut what does
not reduce work for the gym owner. Then turn the survivors into `IBusinessRule` implementations
(domain, pure) or Application-layer guards (cross-aggregate), with the codes below as their `Code`
values. Answer the Open Questions first — every one of them changes the shape of the entities.
