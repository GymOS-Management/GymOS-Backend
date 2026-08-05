# Gyms — Business Rules

**Module:** `GymOS.Platform` · schema `platform` · **platform-scoped** (a gym *is* a tenant, so this
table carries no `tenant_id`)
**Status:** **implemented — M2.** This document records the rules as built, not as proposed. Unlike the
other files in this directory it is a specification, not a draft.

## Purpose

A gym is the platform's tenant. Every gym-facing module — Members, Memberships, Attendance — points at
one of these rows, so the lifecycle here decides whether any of them may operate at all.

## Entities

| Entity | Notes |
|---|---|
| `Gym` | Aggregate root. Platform-scoped, soft-deleted, audited. |
| `GymBranding` | Separate aggregate, 1:1 by `GymId`. Edited by different people at different times than the gym's commercial details, and every field is optional while none of the gym's are. |

`gym_stats` (§2.2 of the architecture) is deliberately absent: it is a read model upserted by a
background job, and the job runner arrives with M5.

## Lifecycle

```
  ┌──────────────┐   activate   ┌────────┐   suspend    ┌───────────┐
  │ Provisioning ├─────────────►│ Active │◄────────────►│ Suspended │
  └──────┬───────┘              └───┬────┘  reactivate  └─────┬─────┘
         └──────────────────────────┴──────────────────────────┘
                            delete (soft) │
                                          ▼
                                    ┌─────────┐
                                    │ Deleted │  terminal
                                    └─────────┘
```

Enforced by `GymStatusTransitions`, an explicit allow-list, and covered as an exhaustive matrix in
`GymStatusTransitionTests` — every ordered pair is either allowed or refused, so a state added later
cannot be quietly reachable.

- **`Provisioning` is where a gym starts.** It exists so a tenant can be set up — branding uploaded,
  staff invited — before members can reach it, and `activate` is the deliberate moment it opens. When
  subscription assignment lands in M3 it becomes the natural completion of provisioning; until then
  activation is an explicit operator action.
- **A gym can be suspended before it ever went live** — a deal that fell through during setup.
- **`Deleted` is terminal.** The architecture names `Deleted → Active` as the transition that must not
  exist: a status flip would resurrect a tenant whose data may already be partly purged and whose
  subscription and invoices were closed on the assumption it was gone. Reinstating a gym is an explicit
  operator decision with its own workflow, not a status change.
- **Suspension is reversible; deletion is not**, which is why they are separate permissions
  (`gyms.suspend`, `gyms.delete`) and why `PlatformAdmin` holds the first but not the second.

## Business Rules

| Code | Rule |
|---|---|
| `gyms.slug_invalid` | The slug is 3–63 characters of lowercase letters, digits, and single hyphens, starting and ending alphanumeric — a DNS label, because a slug is the natural default subdomain once custom domains are switched on. Upper case is **reported, not corrected**: silently lowercasing "Acme-Gym" means the operator sees a value they did not type and reports it as a bug. |
| `gyms.slug_taken` | Unique across every gym **including deleted ones**. A slug appears in URLs members bookmark; handing it to a different gym would point them at someone else's business. |
| `gyms.country_code_invalid` | ISO 3166-1 alpha-2. Case-insensitive on input (upper-cased before validation) — an operator typing "cy" is not told it is invalid. |
| `gyms.currency_invalid` | ISO 4217 alpha-3. One operating currency per gym (AD-42). |
| `gyms.timezone_invalid` | An IANA identifier (AD-41), validated for **shape only**. A domain rule that consulted the host's tz database would answer differently on different machines, and a gym would become invalid because a container image was older. |
| `gyms.owner_email_invalid` | Shallow well-formedness, the same check as everywhere else. Trimmed and lower-cased *before* validating, so a pasted address with a trailing space is accepted rather than rejected as malformed. |
| `gyms.colour_invalid` | Branding colours are hex (`#1A2B3C`) or unset. A stylesheet cannot render "rebeccapurple" from this field, so it is not stored. |
| `gyms.custom_domain_invalid` | A hostname of at least two labels, at most 253 characters. Shape only — whether the gym *controls* the domain is a DNS verification step in the hosting workflow, not a business rule. |
| `gyms.custom_domain_taken` | Unique across the platform among gyms that set one. A domain must resolve to exactly one gym. |
| `gyms.reason_required` | A suspension records why. A suspension nobody can explain later is a support ticket. |
| `gyms.confirmation_required` / `gyms.confirmation_mismatch` | Deletion requires `X-Confirm-Action` naming the gym's slug (AD-6). This removes an entire business's access, so a mis-clicked row in a list — or a replayed script that never saw the resource — must not be able to do it. |
| `gyms.deleted` | A deleted gym cannot be edited, activated, or re-branded. |
| `gyms.already_active` / `gyms.already_suspended` / `gyms.not_suspended` | Self-transitions and wrong-state actions are reported rather than silently succeeding: a caller that has lost track of state should be told. |

## Invariants

- **The slug is immutable after creation.** It is the gym's public address, the confirmation phrase for
  deletion, and potentially a hostname. A mutable slug is a broken link and a confirmation that no longer
  matches.
- **A slug is never released**, including by a deleted gym.
- `IsOperational` is **derived** from status, never stored. A flag would need a job to keep it true, and
  the night the job failed a suspended gym would still be serving.
- **No locale defaults live in the domain.** Country, time zone, and currency are required with no
  fallback (§2.2: "no defaults hardcoded — from platform settings"); a default buried here would be a
  hidden policy every gym silently inherited.
- Deletion is a **soft delete**. The row survives the retention window so invoices, audit entries, and
  remaining tenant data stay readable; hard deletion is a scheduled purge job (GDPR erasure path).
- **A deleted gym still resolves on `GET`.** Refusing to load it would make "this gym was deleted"
  indistinguishable from "no such gym", and the operator needs to be told which. It is excluded from
  lists by default, which is also what stops a deleted tenant being counted in a figure someone reports
  upward.
- **Branding shares the gym's row version.** A branding-only write advances the gym's `xmin`
  explicitly, or two operators editing branding concurrently would both succeed — the child-row gap
  documented in `API_STANDARDS.md` §3.
- Every gym has a branding record from the moment it is provisioned, so the detail view and the branding
  endpoint always have something to read and write.

## Storage notes

- `slug` is `varchar(63)` with a unique index and a `slug = lower(slug)` CHECK constraint, rather than
  the `citext` extension. Lowercase-by-construction plus the constraint satisfies "unique, URL-safe,
  immutable" without adding an extension to every environment.
- `country_code` and `currency` are `varchar`, not `char(2)`/`char(3)`: `bpchar` compares ignoring
  trailing spaces, which would make a padded value equal to a clean one in an index. The exact length is
  a domain rule.
- `custom_domain` carries a **filtered** unique index (`WHERE custom_domain IS NOT NULL`), so the unset
  majority does not collide and the index means "claimed domains".

## Deferred, with reasons

| Deferred | Until | Why |
|---|---|---|
| Subscription and plan on the gym detail view | M3 | The module does not exist. A field that always reads zero teaches operators to distrust the screen. |
| Resolved feature flags | M4 | Same. |
| Aggregate stats (`gym_stats`) | M5 | Needs the background job runner. |
| Device summary | M6 | Same. |
| Reinstating a deleted gym | not planned | Deliberately not a status transition — see Lifecycle. |
| DNS verification of a custom domain | hosting workflow | Ownership of a domain is not a business rule, and a gym should not be blocked from saving branding while DNS propagates. |
| Gym-level settings beyond locale and branding | with the modules that need them | Nothing reads them yet, and a settings table with no readers is a schema decision made blind. |
