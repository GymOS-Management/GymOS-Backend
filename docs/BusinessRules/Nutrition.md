<!--
  DRAFT — not an approved specification.

  This document collects what is known and what is undecided about a module that has not been
  built. It is a starting point for the scoping conversation, not a commitment: the Golden Rule
  (GymOS-Specification/PRODUCT-PRINCIPLES.md) is the scope authority, and every rule below must be
  confirmed with the product owner before it is implemented. Rules marked (?) are assumptions.
-->

# Nutrition — Business Rules

**Module:** `GymOS.Modules.Nutrition` · schema `nutrition` · tenant-scoped
**Status:** not started. **The module most likely to be cut** — see Open Questions.

## Purpose

Nutrition plans a coach prescribes and, potentially, what a member logs against them.

Flagged honestly: this is the module where the Golden Rule is hardest to satisfy. Food logging is
laborious for members and generates data no gym owner acts on, and the market is full of free apps that
do it better. What a gym plausibly needs is a way to *hand a member a plan*, not a food diary.

## Entities

| Entity | Notes |
|---|---|
| `NutritionPlan` | A prescribed plan with a validity window and an author. |
| `PlanMeal` | Meals or targets within a plan. |
| `MacroTarget` | Calorie and macronutrient goals. |
| `FoodItem` | A catalogue entry — externally sourced if this is built at all. |
| `MemberFoodLog` | What a member ate. The expensive part, in both build effort and member effort. |
| `DietaryRestriction` | Allergies and intolerances. **Health-adjacent personal data.** |

## Business Rules

| Code | Rule |
|---|---|
| `nutrition.plan_author_qualified` | Only a user holding the nutrition permission may author a plan. Dietary advice carries professional liability. |
| `nutrition.plan_one_active_per_member` | (?) |
| `nutrition.plan_dates_valid` | End after start; no overlapping active plans. |
| `nutrition.macro_targets_consistent` | Macronutrient grams and the calorie target must be arithmetically consistent within a tolerance — an inconsistent plan is a visibly wrong plan. |
| `nutrition.respects_declared_restrictions` | A plan containing a declared allergen is refused, not warned. This is the one rule here with a safety consequence. |
| `nutrition.log_not_in_future` | |
| `nutrition.plan_immutable_after_start` | A started plan is superseded by a new version rather than edited, so a member and their coach are looking at the same thing. |

## Invariants

- A plan has exactly one author and one subject member.
- Dietary restrictions are recorded against the member (Members module), never duplicated here.
- Food logs are append-only.
- A plan never references a food item that has been removed from the catalogue; deactivation preserves
  the reference.

## Future Rules

- Third-party food database integration (the integration seam already exists, AD-31).
- Barcode scanning in the member app.
- Photo-based logging.
- Recipes and shopping lists.
- Coach review and feedback loop.

## Dependencies

- **Members** (subject, restrictions), **Trainer** (the usual author), platform **integrations** (food
  database), **file pipeline** (photos), **permissions**.
- **Events:** `gymos.nutrition.plan.assigned`, `.completed`, `gymos.nutrition.log.recorded`.

## Open Questions

1. **Is this in the product at all?** Test it against the Golden Rule before designing further: would a
   gym owner pay for this because it saves them time or earns them money? If the honest answer is "it
   is a nice-to-have", it belongs after everything that is not.
2. If it is built, is member food logging in scope, or only coach-authored plans? Plans alone are a
   fraction of the work and most of the value.
3. **Dietary restrictions and allergies are health data** (GDPR Art. 9). Same treatment as member
   health notes: explicit basis, stricter access, defined retention. **Verify with legal counsel.**
4. Does prescribing nutrition require a registered dietitian under Cyprus law? A professional-regulation
   question with liability attached — **verify before shipping any prescriptive feature.**
5. Whose food database, and what does its licence permit for commercial redistribution?

---

## How this document is used

Before the module is implemented: walk the Business Rules against the Golden Rule and cut what does
not reduce work for the gym owner. Then turn the survivors into `IBusinessRule` implementations
(domain, pure) or Application-layer guards (cross-aggregate), with the codes below as their `Code`
values. Answer the Open Questions first — every one of them changes the shape of the entities.
