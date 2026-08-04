<!--
  DRAFT — not an approved specification.

  This document collects what is known and what is undecided about a module that has not been
  built. It is a starting point for the scoping conversation, not a commitment: the Golden Rule
  (GymOS-Specification/PRODUCT-PRINCIPLES.md) is the scope authority, and every rule below must be
  confirmed with the product owner before it is implemented. Rules marked (?) are assumptions.
-->

# Trainer — Business Rules

**Module:** `GymOS.Modules.Trainer` · schema `trainer` · tenant-scoped
**Status:** not started.

## Purpose

The trainer's working day and the programmes they write: assigned members, personal-training sessions,
workout programmes, and progress. One of the two places the Golden Rule bites hardest — a trainer will
abandon a tool that is slower than paper.

## Entities

| Entity | Notes |
|---|---|
| `Trainer` | A tenant user with the trainer role, plus professional detail (qualifications, specialities, availability). |
| `TrainerAvailability` | When they can be booked. Wall-clock, with the same DST rules as Scheduling. |
| `PtSession` | A one-to-one session: trainer, member, time, status. |
| `WorkoutProgramme` | A prescribed plan with a validity window. |
| `ProgrammeExercise` | Exercise, sets, reps, load, notes, ordering. |
| `ExerciseCatalogue` | Shared exercise library — platform-provided, gym-extendable. |
| `MemberProgress` | Logged performance and measurements. |

## Business Rules

| Code | Rule |
|---|---|
| `trainer.session_within_availability` | Unless explicitly overridden, and the override is recorded. |
| `trainer.no_double_booked_session` | A trainer cannot hold two sessions at once, nor a session overlapping a class they instruct — Scheduling and Trainer must check against each other. |
| `trainer.session_requires_entitlement_or_charge` | A PT session is either covered by a package or charged. It is never free by omission. |
| `trainer.cancellation_window` | Inside the window, the session is consumed or charged (?), which is what protects the trainer's time. |
| `trainer.programme_one_active_per_member` | One active programme per member per trainer at a time (?); a member with two current programmes does not know what to do today. |
| `trainer.programme_dates_valid` | End after start; no overlap with another active programme. |
| `trainer.only_assigned_trainer_edits` | A trainer edits their own programmes; a gym admin may reassign. Enforced by permission, not by convention. |
| `trainer.progress_immutable_after_entry` | A logged measurement is corrected by a new entry, not an edit — a rewritten history makes progress charts lie. |
| `trainer.qualification_expiry_warns` | An expired qualification warns management; whether it blocks assignment is a gym decision (?). |

## Invariants

- A session references exactly one trainer and one member, and a time range in UTC with its tenant zone.
- A programme always has an owning trainer and a subject member.
- Progress entries are append-only and timestamped.
- A trainer is always a tenant user; trainer data never creates a second identity for the same person.

## Future Rules

- Group PT and small-group training.
- Programme templates a trainer reuses across members.
- Video or media attachments on exercises (through the file pipeline).
- Member-app visibility of the programme, and can the member log their own sets?
- Trainer commission and payroll reporting.

## Dependencies

- **Members**, **Memberships** (PT packages), **Scheduling** (conflicts and classes), **Payments**
  (session charges), platform **file pipeline**, **time service**, **permissions**.
- **Events:** `gymos.trainer.session.booked`, `.completed`, `.cancelled`,
  `gymos.trainer.programme.assigned`, `.completed`, `gymos.trainer.progress.logged`.

## Open Questions

1. Is the exercise catalogue platform-provided, gym-specific, or both? Both means a merge problem when
   the platform updates an exercise a gym has customized.
2. Does a member log their own progress in the member app, and can a trainer see it immediately?
3. Are PT packages a Memberships concern (entitlements) or a Trainer concern (session credits)? One or
   the other, not both, or the counts will diverge.
4. Cancellation window and whether the session is forfeited — per gym, per trainer, or per package?
5. Do expired qualifications block session booking? A safety and insurance question, not a technical one.

---

## How this document is used

Before the module is implemented: walk the Business Rules against the Golden Rule and cut what does
not reduce work for the gym owner. Then turn the survivors into `IBusinessRule` implementations
(domain, pure) or Application-layer guards (cross-aggregate), with the codes below as their `Code`
values. Answer the Open Questions first — every one of them changes the shape of the entities.
