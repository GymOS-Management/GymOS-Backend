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

# Members — Business Rules

**Module:** `GymOS.Modules.Members` · schema `members` · tenant-scoped
**Status:** not started. First gym-facing module after M2.

## Purpose

The person record every other gym module points at. A member is the tenant-scoped identity of
someone who trains at a gym — distinct from a platform administrator (`platform.users`) and from
gym staff, who are tenant *users* with roles.

Owning identity here is what lets Memberships, Attendance, Payments, and Scheduling reference one
subject without each inventing its own.

## Entities

| Entity | Notes |
|---|---|
| `Member` | Aggregate root. Tenant-scoped, soft-deleted, RLS-protected (R14). |
| `MemberContact` | Email, phone, address. Separate because contact detail changes far more often than identity, and each change is auditable on its own. |
| `MemberHealthNote` | Medical declarations, injuries, consent. **Special-category personal data under GDPR Art. 9** — see Open Questions. |
| `EmergencyContact` | Name, relationship, phone. |
| `MemberPhoto` | Through the file pipeline (AD-44); never a raw upload. |

## Business Rules

| Code | Rule |
|---|---|
| `members.email_required_when_app_access` | A member who gets member-app access needs a unique email within the tenant. Without app access, email is optional — a walk-in on a paper form is still a member. |
| `members.email_unique_per_tenant` | Unique per gym, not globally: the same person may belong to two gyms, and forcing one record across tenants would leak membership between them. |
| `members.minor_requires_guardian` | A member below the age of majority requires a recorded guardian and guardian consent (?). Cyprus-specific — verify with counsel. |
| `members.date_of_birth_not_future` | And not implausibly distant (?). |
| `members.cannot_delete_with_active_membership` | Deletion is refused while a membership is active; cancel first. Prevents an accidental delete from orphaning billing. |
| `members.soft_delete_only` | Deletion is a soft delete; hard erasure is the GDPR path, via a purge job with a retention window. |
| `members.duplicate_warning_not_block` | A matching name and date of birth warns; it does not block. Twins exist, and a blocked registration at the front desk becomes a fake record. |

## Invariants

- A member always belongs to exactly one tenant, and `tenant_id` is immutable.
- A member's status transitions form a state machine (`Prospect → Active → Inactive → Archived`),
  validated in the domain like `UserStatusTransitions` — not boolean flags.
- Health notes are never returned by a list endpoint, only by an explicit detail read carrying its own
  permission.
- A soft-deleted member is invisible to every query except the audit trail and the purge job.

## Future Rules

- Member-app self-service: which fields may a member edit about themselves?
- Household or family grouping, and shared billing.
- Membership transfer between branches of the same gym.
- Waivers and signed documents, with versioning of the document a member actually signed.
- Photo-on-check-in matching (needs a biometric-data assessment first).

## Dependencies

- **Platform:** tenancy, RLS, feature flags, `IFileAccessService` for photos.
- **Consumed by:** Memberships, Attendance, Payments, Scheduling, CRM, Trainer, Nutrition.
- **Events published:** `gymos.members.member.registered`, `.updated`, `.archived`, `.deleted`.

## Open Questions

1. **Health data.** GDPR Art. 9 special-category data needs an explicit lawful basis, likely explicit
   consent, plus stricter retention and access control than the rest of the record. Does the MVP
   store it at all, or defer until the legal basis is documented? **Verify with legal counsel.**
2. **Age of majority and guardian consent** under Cyprus law — 18, and what evidence of consent is
   required?
3. Is a member without any membership meaningful (a prospect), or does registration always create one?
4. Retention window for a soft-deleted member before purge, and does the audit trail survive it?
5. Does a member identity span branches of one gym, or one per branch?

---

## How this document is used

Before the module is implemented: walk the Business Rules against the Golden Rule and cut what does
not reduce work for the gym owner. Then turn the survivors into `IBusinessRule` implementations
(domain, pure) or Application-layer guards (cross-aggregate), with the codes below as their `Code`
values. Answer the Open Questions first — every one of them changes the shape of the entities.
