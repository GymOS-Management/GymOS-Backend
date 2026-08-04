<!--
  DRAFT — not an approved specification.

  This document collects what is known and what is undecided about a module that has not been
  built. It is a starting point for the scoping conversation, not a commitment: the Golden Rule
  (GymOS-Specification/PRODUCT-PRINCIPLES.md) is the scope authority, and every rule below must be
  confirmed with the product owner before it is implemented. Rules marked (?) are assumptions.
-->

# Reports — Business Rules

**Module:** `GymOS.Modules.Reports` · schema `reports` · tenant-scoped
**Status:** not started.

## Purpose

The numbers a gym owner acts on. Deliberately narrow: the Golden Rule rules out "complicated reports
nobody reads", and PRODUCT-PRINCIPLES.md §4 is explicit that **a report ships only with a named
decision it informs.**

So this module's defining rule is not technical. Every report must name the decision it supports, or it
does not get built.

## Entities

| Entity | Notes |
|---|---|
| `ReportDefinition` | A named report: its inputs, its shape, and the decision it informs. |
| `DailyTenantStats` | Pre-aggregated daily figures per gym. The read model reports are served from. |
| `ReportRun` | An execution: parameters, who ran it, when, and how long it took. |
| `ScheduledReport` | A recurring delivery to a recipient. |
| `ReportExport` | A generated file, through the file pipeline with its own retention. |

## Business Rules

| Code | Rule |
|---|---|
| `reports.named_decision_required` | A report definition without a stated decision is refused at review, not at runtime. The rule that keeps this module small. |
| `reports.read_from_read_models_only` | Reports query pre-aggregated tables, never operational ones (AD-8). A report that scans `attendance` will one day take the gym's booking system down with it. |
| `reports.tenant_scoped_always` | Every query is tenant-filtered and RLS-protected. A cross-tenant figure in a gym's report is a data breach, and aggregate reporting is exactly where that mistake is easy. |
| `reports.respects_caller_permissions` | A report never shows what the caller could not read directly — financial figures need the billing permission, not just the reports one. |
| `reports.figures_carry_as_of` | Every figure states the instant it was computed. A stale number presented as live is worse than no number. |
| `reports.money_conversion_recorded` | Any converted amount carries its rate, provider, and as-of date (AD-43), so a figure can be reconciled against an export months later. |
| `reports.export_expires` | Generated exports have a retention window and are removed by the purge job — an export is a copy of personal data sitting in storage. |
| `reports.no_pii_in_scheduled_email_body` | A scheduled report links to the export; it does not attach member-level data to an email. |
| `reports.run_is_audited` | Who ran what, with which parameters. Exporting member data is an action worth recording. |

## Invariants

- A report is reproducible: the same parameters against the same as-of point produce the same figures.
- Read models are derived and rebuildable from operational data; none is a source of truth.
- No report writes to operational tables.
- An export always references the run that produced it and the user who asked.

## Future Rules

- Custom report builder (weigh hard against the Golden Rule — this is where reporting modules go to die).
- Benchmarking a gym against anonymized platform aggregates. Needs a privacy assessment and probably
  a contractual basis.
- Scheduled delivery with per-recipient filtering.
- Data warehouse export for gyms with their own BI.

## Dependencies

- **Every operational module** as a source, but only through read models. Platform **jobs**
  (aggregation), **file pipeline** (exports), **notifications** (delivery), **Money** and **exchange
  rates**, **permissions**, **audit**.
- **Events:** `gymos.reports.run.completed`, `gymos.reports.export.generated`.

## Open Questions

1. **Which reports, and for which decisions?** The list should be short and each entry should name the
   decision. Until that exists, this module has no scope.
2. Aggregation cadence: nightly, hourly, or on demand? Determines how stale a figure may be and how
   much the rollup job costs.
3. Are gym-side reports the same engine as the Super Admin analytics (already approved, AD-8), or a
   separate one? One engine is cheaper; two avoid coupling tenant reporting to platform reporting.
4. Export formats — CSV is table stakes; is PDF genuinely needed, and by whom?
5. Retention for exports containing member-level personal data, and who is told when they are purged?

---

## How this document is used

Before the module is implemented: walk the Business Rules against the Golden Rule and cut what does
not reduce work for the gym owner. Then turn the survivors into `IBusinessRule` implementations
(domain, pure) or Application-layer guards (cross-aggregate), with the codes below as their `Code`
values. Answer the Open Questions first — every one of them changes the shape of the entities.
