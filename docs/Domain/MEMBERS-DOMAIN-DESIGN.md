# Members Domain — Business Design

**Status:** Authoritative business specification for the Members, Memberships, Attendance, and
Access domain. Implementation must follow this document.
**Blocked on:** the [Open Product Decisions](#20-open-product-decisions). **Do not begin
implementation until each one marked *blocking* is resolved.**
**Authority:** GymOS-Specification (frozen architecture) · PRODUCT-PRINCIPLES.md (the Golden Rule,
which is the scope authority over everything here)
**Supersedes:** `docs/BusinessRules/Members.md`, `Memberships.md`, `Attendance.md` for this domain.

---

## 1. Purpose and how to read this

This document answers the product questions *before* anyone writes code, so that the arguments happen
here — where changing our minds is free — rather than in a schema that already holds a gym's data.

It is written in business language on purpose. There is no schema, no API, and no code, because those
are consequences of the decisions below and not the decisions themselves.

Three kinds of statement appear, and the difference matters:

| Marking | Meaning |
|---|---|
| **Decided** | Settled. Either the frozen architecture already decided it, or the operational reality admits one sensible answer. Implement as written. |
| **Default** | A recommended answer that a gym may need to configure. Implement the default; make it a setting. |
| **OPEN — §20.n** | A genuine business decision with money, legal, or workflow consequences that cannot be inferred. **Not implemented until answered.** |

Everything is judged against one question, and where the answer is not obvious that question decides
it: **does this reduce work for reception staff and the gym owner?** A rule that is elegant but makes
the front desk slower during the 18:00 rush is the wrong rule.

---

## 2. Decisions already made — do not relitigate

The frozen architecture settles the following. They are listed so that implementation does not spend
time rediscovering them, and so that this document is not read as re-opening them.

| Decision | Source | Consequence for this domain |
|---|---|---|
| **A gym is a tenant.** Members are tenant-scoped data. | ARCHITECTURE.md §1 | A member record belongs to exactly one gym. "One person, several gyms" is several member records — see §14. |
| Row-level security isolates tenants. | AD-1 tenancy, R14 | A cross-tenant member read is a data breach, not a bug. |
| Members are **not** platform administrators. | ARCHITECTURE.md §2.1 | Gym staff and members live in tenant identity; `platform.users` is only for platform operators. |
| **UTC everywhere; tenant time zones exist at presentation and for wall-clock scheduling.** | AD-40, AD-41 | "Did they visit today?" is a question in the *gym's* local day, not UTC. This has real consequences — see §11.4. |
| Gateway appliances are **offline-first** and their clocks are **untrusted**. | AD-21, AD-41 | The door can and will admit people using stale information. §13.8 states what we accept. |
| Anything that charges, books, unlocks, or registers is **idempotent**. | AD-39, R5 | A double-scanned turnstile must not create two visits; a retried registration must not create two members. |
| Every entity mutation is **audited**, with sensitive values redacted. | AD-7 | Overrides, merges, and corrections are all recorded without extra work. |
| Files (photos, signed documents) go through the **quarantine-first pipeline**. | AD-44 | No member photo is served before it is validated and scanned. |
| Money uses the **`Money` type** with explicit rounding; one operating currency per gym. | AD-42 | Membership prices and balances are never naked decimals. |
| Feature availability is resolved **override → plan → default**. | ARCHITECTURE.md §2.4 | Whether a gym *has* memberships, attendance, or multi-location is a feature-flag question, not a code branch. |

---

## 3. Core concepts, and why they are separate

Most of the difficulty in this domain comes from collapsing concepts that behave differently. Four
must stay distinct.

**Person** — a human being. Not a stored thing in GymOS today; the word is used here to talk about
identity across gyms and across roles.

**Member** — a person's *relationship with one gym*. It is the record that persists: their name,
their history, their notes, their attendance. A member exists whether or not they are currently
paying, and continues to exist after they leave.

**Membership** — a *commercial agreement* that grants entitlements for a period. It starts, may be
frozen, and ends. A member may have had a dozen memberships over eight years.

**Visit** — a *business fact*: this member attended on this day at this location. What entitlement is
consumed against and what retention reports count.

**Check-in** — a *technical event*: a credential was presented at a device at an instant, and was
allowed or denied. Several check-ins can belong to one visit. A denied check-in belongs to no visit
but must still be recorded.

> **Why this matters at the desk.** A member says "the door won't let me in." Reception needs to see
> the *denied check-in* with its reason within one click. If denials are not recorded, the only
> available answer is "try again", and the member is standing there.

---

## 4. The Member

### 4.1 What a member is

**Decided.** A member is a person's enduring relationship with one gym. The record is created the
first time the gym needs to remember the person — a tour, a trial, a paid membership, or a walk-in
day pass — and it is never deleted while the gym has a legitimate reason to keep it.

A member record answers: who is this person, how do we reach them, what have they bought, when have
they attended, who should we call in an emergency, and what have staff noted about them.

### 4.2 Can a member exist without a membership?

**Decided: yes, and this is essential.** Four everyday situations require it:

1. A prospect who took a tour and left their details.
2. A trial user before the trial is issued.
3. A former member whose membership ended — the person and their history remain.
4. A walk-in who paid for a single day pass and has no ongoing agreement.

Requiring a membership to create a member would force reception to invent one. A gym that has to
create a fake "£0 prospect membership" to write down a phone number has been given a worse tool than
a notebook.

### 4.3 What information is mandatory

**Decided.** The minimum to create a member is deliberately three things:

1. **First name**
2. **Last name**
3. **One means of contact** — email *or* mobile number. Either satisfies it.

Nothing else is required to create the record.

> **Why so little.** Every additional mandatory field at the front desk is a field that gets filled
> with `x`, `test`, or `01/01/1900` during a busy shift. Mandatory data that is routinely faked is
> worse than optional data that is sometimes missing, because nobody can tell which values are real.
> Data becomes required at the point where it is genuinely needed — see §4.5.

### 4.4 What information is optional

**Decided.** Everything else, including: preferred name, date of birth, gender (self-described, free
text or decline), address, secondary phone, photo, occupation, marketing source ("how did you hear
about us"), preferred language, notes, and emergency contact.

### 4.5 Information required *conditionally*

**Decided.** Some data is not required to create a member but *is* required before a specific thing
can happen. This is how the system asks for data at the moment it matters instead of up front.

| Requirement | Becomes mandatory when |
|---|---|
| Email address | The member is given member-app access, or is sent anything by email. |
| Date of birth | A membership is sold (age-restricted plans, and it is the primary duplicate-detection signal). |
| Guardian details and consent | The member is a minor — see §4.9. |
| Emergency contact | **Default:** before the first check-in. A gym is a place where people collapse. |
| Signed waiver / terms acceptance | Before the first check-in. |
| Payment method | A recurring membership is sold. |
| Health declaration | OPEN — §20.5. |

### 4.6 Can a member belong to multiple gyms?

**Decided: a member record belongs to exactly one gym** (a tenant, per the frozen tenancy model).

A person who trains at two unrelated gyms is two member records. They are not linked, and neither gym
can see the other's record. This is correct: the two gyms are separate businesses and separate data
controllers.

"One gym with several locations" is a different question entirely and is answered in §14.

### 4.7 Can staff also be members? Can trainers?

**Decided: yes, and they must be modelled as two roles on one person, never as one merged record.**

Staff and trainers commonly train at the gym they work for, usually free. They need to be members
because they check in, book classes, and may buy personal training or retail.

- A staff account and a member record are **linked**, not merged.
- Their attendance is recorded like anyone's.
- **Default:** staff memberships are a normal membership on a "Staff" plan priced at zero, so it
  expires with employment and appears in reports like any other.

> **Why not merge them.** Merging makes every question ambiguous: does the trainer appear in the
> member count the owner reports to their accountant? Does their check-in count towards occupancy?
> Do they receive member marketing? Two linked records answer each question separately.

**Decided:** a staff member's *employment* ending does not delete their member record, and their
membership ending does not affect their staff account. The two lifecycles are independent.

### 4.8 Can a former member return?

**Decided: yes, and the same member record is reused. Always.**

Creating a second record for a returning member is the most common and most damaging data error in gym
software. It loses their attendance history, their notes, their PT programme, their waiver, and it
inflates the member count the owner makes decisions with.

- Returning is a **new membership on the existing member**, not a new member.
- Duplicate detection (§4.10) exists primarily to catch this at the front desk.
- **Decided:** a member whose record was archived is *un-archived* on return, and the archival is kept
  in history rather than erased.

### 4.9 Can minors be members?

**Decided: yes.** Gyms have junior memberships, family memberships, and swimming lessons.

- A minor's record requires a **guardian relationship** and recorded **guardian consent** before a
  membership is sold or a check-in is permitted.
- The guardian may or may not themselves be a member. Both must be supported — a parent who signs up
  their child but does not train is common.
- **Default:** the guardian is the default recipient of the minor's communications and the default
  payer.
- Age thresholds, what counts as evidence of consent, and which activities have their own age limits
  are OPEN — §20.6. **These are legal questions under Cyprus law and must be confirmed with legal
  counsel.**

### 4.10 Can duplicates exist, and how is detection meant to work?

**Decided: duplicates will exist and the system's job is to manage them, not to prevent them.**

They arise unavoidably: the same person joins twice years apart, gives a different email, marries and
changes surname, or is entered by two staff on two terminals within a minute.

**Detection warns; it never blocks.**

> **Why never block.** A blocked registration at a busy front desk does not produce a clean database.
> It produces "John Smith 2", "John Smith (new)", and `john.smith+1@…`. The member is standing there
> and the queue is growing; staff will always find a way through. A warning that is easy to act on
> produces better data than a block that is easy to defeat.

**Decided — detection signals, in descending confidence:**

1. Same email address (case-insensitive) — near-certain.
2. Same mobile number (normalized) — near-certain.
3. Same last name **and** date of birth — strong.
4. Same first name, last name, and date of birth — strong.
5. Similar name (fuzzy) **and** same date of birth — worth surfacing.
6. Similar name alone — **not** a signal. Too many false positives to be useful; showing them trains
   staff to dismiss the warning.

**Decided — behaviour at the point of entry.** When a signal fires, reception is shown the matching
member with enough context to decide in one glance — name, date of birth, last visit, membership
status — and offered three actions: **use this member**, **create anyway**, or **merge later**. The
default and most prominent action is *use this member*, because that is the right answer most of the
time.

**Decided — merging is a first-class operation**, not a database chore. It must be available to
staff with the appropriate permission, it must show what will happen before it happens, and it must be
audited. Attendance, memberships, payments, and notes all move to the surviving record; the merged
record leaves a permanent redirect so an old reference resolves. Merge details are OPEN — §20.11.

### 4.11 Emergency contacts

**Decided: yes, and at least one is expected before the first check-in** (Default; §4.5).

- A member may have several, in priority order.
- An emergency contact is **not** a member and needs no account — a name, a relationship, and a phone
  number.
- **Decided:** emergency contact details are visible to staff on duty without needing a permission
  that a receptionist would not have at 06:00. Locking away the phone number to call in an emergency
  is a safety failure dressed as data protection.
- A minor's guardian is automatically an emergency contact unless overridden.

### 4.12 Medical information

**Decided: optional, minimal, and separated from everything else.**

Health data is special-category personal data under GDPR Article 9 and cannot be collected on the same
basis as a phone number.

**Decided constraints, whatever is eventually collected:**

- Never shown on a list, a check-in screen, or a report. Detail view only.
- Behind its own permission, separate from general member management.
- Redacted from audit values and never written to a log.
- Its own retention rule, shorter than the member record's.
- A member may decline without losing access to anything except activities that legally require it.

**Decided:** a *non-medical* flag is available to reception for operational awareness — "see notes
before check-in" — which carries no health detail and is not special-category data. This gives the desk
what it actually needs, which is to know that they should ask, without spreading medical records
across the building.

Whether the MVP collects health information at all is OPEN — §20.5. **Requires legal counsel.**

---

## 5. Relationships

**Decided.** A member may be related to other people and organizations, and these are distinct
relationship types rather than one generic link.

| Relationship | Meaning | Notes |
|---|---|---|
| **Guardian of / ward of** | Legal responsibility for a minor | Carries consent, default payer, default recipient of communications. Required for minors. |
| **Family group member** | Shares a family or household membership | Affects pricing and entitlement, so it is a commercial relationship, not an address match. Scope OPEN — §20.8. |
| **Corporate member** | Employed by an organization holding a corporate agreement | Membership eligibility depends on continued employment. Scope OPEN — §20.8. |
| **Emergency contact** | Who to call | Not a member; no account. |
| **Referred by** | Who introduced them | Feeds referral rewards; marketing rather than operational. |
| **Payer** | Who pays, when not the member | Guardian, employer, or another member. Distinct from every relationship above. |
| **Linked staff account** | This member is also staff or a trainer | §4.7. |
| **Previous record (merged)** | This member absorbed another record | Permanent, so old references resolve. |

**Decided:** relationships are directional and independently revocable. A guardian ceasing to be
guardian does not remove them as emergency contact, and a family group ending does not delete anyone.

**Decided:** the *payer* relationship is deliberately separate from family and corporate. Real
arrangements do not follow the household — a grandparent pays for one grandchild's membership while
the family group contains four people. Deriving the payer from the family group produces wrong invoices.

---

## 6. Membership fundamentals

### 6.1 What a membership is

**Decided.** A membership is an agreement between the gym and one member: in exchange for a price,
the member receives entitlements for a period, subject to conditions.

A membership records what was agreed, **including the price at the time it was agreed**. Changing a
plan's price never re-prices an existing membership; an existing member's price changes only by an
explicit, recorded action.

### 6.2 Primary versus add-on — the central distinction

**Decided.** Memberships are of two kinds, and nearly every "can memberships overlap?" question
resolves once they are separated.

**Primary membership** — grants access to the gym. This is what the member means by "my membership".
**At most one is in force at any moment.**

**Add-on** — grants something extra alongside a primary membership: a block of personal-training
sessions, a class pack, locker rental, a tanning or sauna package, a second-location upgrade.
**Several may be in force at once, and they may overlap freely.**

> **Why this matters.** Without the distinction, either the system forbids a member from buying a PT
> pack while holding a gym membership (absurd), or it permits two overlapping gym memberships and can
> no longer answer "which one does today's visit consume?" The distinction removes both problems.

### 6.3 Can a member have multiple memberships?

**Decided: yes.**

- **At most one active primary** at any instant.
- **Any number of add-ons**, overlapping freely.
- **Any number of historical memberships** — history is never overwritten.
- **One future-dated primary** may be queued behind the current one (§6.5).

### 6.4 Can memberships overlap?

**Decided:**

| Combination | Overlap allowed | Why |
|---|---|---|
| Two primaries | **No** | Ambiguous entitlement and double billing. |
| Primary + add-on | **Yes** | The normal case. |
| Add-on + add-on | **Yes** | A member may hold PT sessions and a class pack. |
| Primary + queued future primary | **Yes, adjacent** | The new one starts the day after the current one ends. A gap is allowed; an overlap is not. |

### 6.5 Can memberships be future-dated?

**Decided: yes, and this is a requirement, not a nicety.** Memberships are sold mid-month to start on
the 1st; a member gives notice and signs a new plan starting when the old one ends; a January
promotion is sold in December.

- A future-dated membership is **visible, committed, and grants nothing yet**.
- It begins granting access on its start date with no human action and no job — see §6.8.
- **Default:** at most one queued future primary. Two queued renewals is a sign of a mistake, not a
  workflow.
- Cancelling a future-dated membership before it starts leaves no period and no charge.

### 6.6 Does a membership start immediately?

**Default: yes** — reception sells a membership and it works before the member walks away from the
desk. Future-dating is a deliberate choice, not the default.

**Decided:** a start date may be backdated, with a permission and a recorded reason. This is needed
for real corrections ("she started on the 1st, we entered it on the 4th"), and it is exactly the kind
of action that must be audited. Backdating never retroactively creates or removes visits.

### 6.7 Can memberships be frozen?

**Decided: yes.** Injury, travel, pregnancy, and seasonal absence are all normal, and a gym that
cannot freeze a membership loses the member instead.

- A freeze has a **start, an end, and a reason**.
- A frozen membership **grants no access** (§13.3).
- A freeze may be ended early.
- A freeze is visible to reception with its dates, so "why can't I get in?" is answerable instantly.

Freezes carry two decisions with direct revenue consequences — whether frozen time extends the end
date, and whether a freeze is billed — both OPEN, §20.1.

**Default limits, pending §20.1:** a plan may cap freezes by count per year and total days per year;
a minimum freeze duration prevents a freeze being used to skip a single billing day.

### 6.8 Can memberships expire automatically?

**Decided: yes — and expiry is *derived from the agreed dates*, never a status that a scheduled job
switches over.**

> **Why this is stated so firmly.** If expiry depends on a nightly job, then the night the job fails,
> expired members walk in at 06:00 and nobody knows why. Access is computed at the moment the
> credential is presented, from the period dates. A failed job then costs a stale report, not
> unauthorized access.

Jobs still exist, for things that genuinely need to happen at a moment in time — sending an
"expiring in 7 days" reminder, attempting a renewal charge, producing daily figures. None of them is
what makes a membership expired.

---

## 7. Membership plan types

**Decided.** The plan defines what the member gets. Four families must be supported, and they combine.

| Family | Entitlement | Everyday example |
|---|---|---|
| **Time-based, unlimited** | Unlimited visits within a period | "Monthly unlimited" |
| **Time-based, limited** | A capped number of visits within a period, resetting each period | "8 visits per month" |
| **Visit-based (pack)** | A fixed number of visits, no period reset, usually with a long expiry | "10-visit pass, valid 6 months" |
| **Credit-based** | A balance of credits spent at varying rates | "20 credits; a class costs 2, a PT session 5" |

**Decided — modifiers that apply across families:**

- **Time restrictions (off-peak).** A plan may permit access only within stated windows. Genuinely
  common and genuinely a discount lever: "off-peak" memberships are cheaper because they smooth demand.
  This means the access decision must consider the *plan's* permitted hours, not only the gym's opening
  hours (§13.4).
- **Location scope.** Which locations the plan admits (§14).
- **Activity scope.** Gym floor only, or classes included, or pool included.
- **Guest allowance.** A number of guest visits per period.
- **Minimum commitment.** A contracted term. OPEN — §20.13.

**Decided:** an unlimited plan still records visits. "Unlimited" describes entitlement, not
measurement — the owner's most valuable retention signal is attendance frequency, and it must not be
switched off by the plan type.

---

## 8. Renewal

**Decided.** A renewal produces a **new period on the same membership**, not a new membership. The
member's tenure with the gym is continuous, and reports about how long people stay depend on it.

### 8.1 Automatic renewal

**Default: available per plan, and off unless the plan says otherwise.**

- Auto-renewing memberships attempt a charge before the period ends, so a failure surfaces before the
  member is at the door.
- **Default:** the renewal charge is attempted several days ahead of the period end (setting), giving
  reception time to resolve a failure quietly.
- A member may cancel auto-renewal at any time; it stops the *next* renewal and does not end the
  current period.
- **Decided:** every renewal attempt and its outcome is recorded, whether it succeeded or not. "Why
  did my card get charged?" and "why didn't it?" are both frequent questions.

### 8.2 Manual renewal

**Decided.** Reception can renew at the desk in one action, defaulting to the same plan and continuing
from the current period end so no gap is created by accident.

### 8.3 Renewing after a lapse

**Decided.** A member returning after their membership ended is renewed **from today** by default, not
backdated to close the gap. Backdating would charge them for time they did not have.

Whether a lapse resets tenure for loyalty purposes is a reporting decision, not an access one, and is
deferred.

---

## 9. Grace periods

**Decided.** Two distinct grace concepts exist and must not be conflated.

**Access grace** — a short window after a period ends during which the member still gets in. Protects
against the everyday case of a renewal being a day late. Default and length: OPEN — §20.3.

**Payment grace** — a window after a failed payment before access is affected. Default and length:
OPEN — §20.3.

**Decided:** whenever a member is admitted under a grace period, reception sees it and the member can
be told. Silent grace produces a worse surprise later: the member believes they are paid up until the
day the door refuses them with no warning.

---

## 10. Cancellation, upgrade, downgrade, transfer

### 10.1 Cancellation

**Decided:**

- Cancellation is effective **now** or **at period end**. Never retroactive — backdating would require
  reversing settled charges.
- **Default:** at period end. The member has paid for the period; ending it immediately is a refund
  conversation nobody asked for.
- A reason is recorded. **Decided:** from a short list plus free text, because "why are members
  leaving?" is a question the owner needs answered and free text alone cannot be counted.
- Cancellation never deletes the membership or its history.
- A cancelled membership can be **reinstated** before its period end — members change their minds, and
  making them re-purchase punishes a saved cancellation.

Notice periods, refunds, and early-termination fees are OPEN — §20.12.

### 10.2 Upgrade and downgrade

**Decided: both supported, and they are the same operation with different directions and timing
defaults.**

| | Default timing | Reasoning |
|---|---|---|
| **Upgrade** (more expensive or more entitlement) | **Immediate** | The member is paying more and wants it now. Delaying is a sale refused. |
| **Downgrade** (less expensive or less entitlement) | **At period end** | They have paid for the current period; downgrading immediately means a refund. |

**Decided:**

- A change of plan continues the **same membership** with a new period, preserving tenure.
- The previous plan and price remain in history.
- Entitlement already consumed in the current period is **not** returned.
- Proration on an immediate upgrade is OPEN — §20.4.

### 10.3 Transfer

**Decided — three different things are called "transfer", and only two are supported:**

| Kind | Supported | Notes |
|---|---|---|
| Transfer between **locations** of the same gym | **Yes** | Change of home location; membership continues. §14. |
| Transfer to a **different plan** | **Yes** | That is an upgrade or downgrade, §10.2. |
| Transfer to a **different person** | **Default: no** | See below. |

**Default: a membership is not transferable between people.** A membership is priced on one person's
usage and carries their waiver, health declaration, and access credential. Allowing transfer invites
one paid membership serving a household.

Where a gym's contract explicitly permits it, it is a **cancellation plus a new membership**, recorded
as linked, rather than moving an agreement between people. This keeps each person's waiver and consent
attached to the person who gave it.

---

## 11. Attendance and check-ins

### 11.1 What a check-in is

**Decided.** A check-in is a recorded *attempt* to enter: a credential presented at a device (or by a
staff member) at an instant, with the outcome **allowed** or **denied** and, when denied, a reason.

**Decided: denied attempts are recorded.** They are how reception answers "the door won't let me in",
how a broken reader is spotted, and how a shared credential is detected. A system that records only
successes cannot explain a failure.

### 11.2 What attendance is

**Decided.** Attendance is the derived record of the member having been present: a **visit**, on a
day, at a location, with a first and (if known) last check-in.

A visit is what entitlement is consumed against and what reports count. Several check-ins may
contribute to one visit; a denied check-in contributes to none.

### 11.3 Can multiple check-ins happen?

**Decided: yes, routinely, and most of them are not new visits.** Turnstiles double-fire, readers are
re-presented when the light is missed, and members step out to a car and come back.

### 11.4 Minimum time between scans

**Decided: two separate windows, for two separate problems.**

**Deduplication window — Default 90 seconds.** A second scan of the same credential at the same device
within this window is *the same check-in*, not a new one. It is discarded, not recorded as a denial.
This is a hardware fact, not a business rule: turnstiles and readers repeat.

**Re-entry window — Default: the whole of the gym's local day.** A further check-in on the same day at
the same location joins the **existing visit**. It does not create a second visit and does not consume
entitlement twice.

> **Why the local day, and why it matters.** Entitlement is money. On an "8 visits per month" plan, a
> member who steps out for lunch and returns must not be charged two of their eight visits — they will
> notice, and they will be right. "Day" means the gym's local day (AD-41), so a 23:50 entry and a
> 00:10 re-entry are two visits in a 24-hour gym. Whether that is desired is OPEN — §20.2.

### 11.5 Can members leave and re-enter?

**Decided: yes, freely, within the same visit** (§11.4).

### 11.6 Check-out

**Default: optional.** Most gyms have no exit reader, and requiring check-out produces a system that
believes everyone who ever entered is still inside.

- Where check-out exists, it closes the visit and enables accurate occupancy and duration.
- Where it does not, a visit is closed by end of day.
- **Decided:** occupancy is presented as an **estimate** wherever check-out is not enforced. A number
  the owner believes is exact, but is not, is worse than one labelled approximate.

### 11.7 Can attendance be corrected?

**Decided: yes, by correction — never by editing or deleting.**

- A wrong visit is **voided** with a reason; it stops counting and remains visible.
- A missing visit is **added** manually and marked as such.
- Every correction records who, when, and why.
- **Decided:** a correction that changes consumed entitlement adjusts entitlement too. Otherwise
  reception "fixes" attendance and the member's remaining visits stay wrong — the original complaint
  unresolved.

### 11.8 Can attendance be manual?

**Decided: yes, and it must be easy.** The reader will fail, a member will forget their card, and a
new joiner will not have a credential yet. Reception checking someone in by name is the fallback that
keeps the gym open.

- Requires a permission, records the staff member, and is marked as manual.
- Manual check-ins consume entitlement exactly as automatic ones do; anything else makes them a way to
  train for free.
- **Decided:** manual check-in is not a privileged exception to the access rules. It runs the same
  decision (§13) and can be denied. Overriding a denial is a *separate*, more privileged action.

### 11.9 Can attendance be imported?

**Decided: yes — every gym adopting GymOS is leaving something else, and their history matters.**

- Imported attendance is marked as imported, with its source.
- **Decided:** imported attendance does **not** retroactively consume entitlement. The entitlement it
  would have consumed was settled in the old system; re-consuming it would show every migrated member
  as having used visits they still have.
- Imported records are never presented as check-ins at a device, because no device saw them.

### 11.10 Offline gateway synchronization

**Decided,** following the approved gateway design (AD-21, AD-41):

- The gateway decides **locally** when the cloud is unreachable, using its last received snapshot of
  who may enter.
- Check-ins recorded offline are uploaded on reconnection and are **not** duplicated on replay
  (AD-39).
- The appliance's clock is untrusted: the reported time is stored **with** measured skew and the time
  of receipt. Business questions use skew-adjusted time; the raw value is annotated, never rewritten.
- **A late-arriving offline check-in can conflict with what happened in the meantime** — a member
  cancelled at 10:00 whose 09:50 offline entry arrives at 11:00. **Decided:** the visit is recorded as
  it happened, and the conflict is surfaced rather than resolved silently. The gym decides what to do
  about one entry; the system must not quietly discard evidence that someone was in the building.

**Accepted limitation, to be stated plainly to gym owners:** while a gateway is offline it may admit
someone whose membership changed moments earlier. Offline-first means the door keeps working during an
internet outage, and that is the right trade — but it is a trade, and it should be documented rather
than discovered.

---

## 12. Attendance retention

**Decided.** Attendance is personal data about a person's movements and cannot be kept forever by
default.

- Individual visit records have a **defined retention period**, after which they are removed or
  irreversibly aggregated.
- Aggregated figures (visits per day, per plan, per hour) may be kept longer, as they cease to identify
  anyone.
- A member exercising erasure removes their identifiable visits; aggregates already computed are not
  recomputed.

The retention period is OPEN — §20.14. **Requires legal counsel.**

---

## 13. The access decision

This section is the operational heart of the domain. The check-in question — *may this person come in,
right now?* — is answered by one ordered evaluation. Order matters: the first rule that denies, denies.

**Decided — evaluation order:**

| # | Check | Outcome if it fails |
|---|---|---|
| 0 | **Emergency mode active?** | Allow, flagged. §13.10 |
| 1 | **Credential recognized?** | Deny — *unknown credential* |
| 2 | **Member banned?** | Deny — *banned*. §13.6 |
| 3 | **Member archived or erased?** | Deny — *no active record* |
| 4 | **Is this location in the membership's scope?** | Deny — *wrong location*. §13.5 |
| 5 | **Any membership in force?** (active, or within access grace) | Deny — *expired* / *frozen* / *not started* / *cancelled*. §13.1–13.3 |
| 6 | **Within the plan's permitted hours?** | Deny — *outside plan hours*. §13.4 |
| 7 | **Is the gym open, or does the plan grant out-of-hours access?** | Deny — *gym closed*. §13.4 |
| 8 | **Entitlement available?** (visit, credit) | Deny — *no visits remaining* |
| 9 | **Hard financial block, if configured and exceeded?** | Deny — *account on hold*. §13.7 |
| 10 | **Soft conditions** — balance owing, expiring soon, waiver missing, "see notes" | **Allow**, with a flag for reception |

Then: allow, consume entitlement, record the visit.

**Decided — every denial carries a reason that is meaningful to two audiences:** a short form the
member can be told at the door, and the full reason with its resolution for reception. "Access
denied" alone converts a solvable problem into a queue.

### 13.1 Membership expired

**Decided: deny** — subject to the access grace period (§9), during which the member is admitted and
reception is told.

### 13.2 Membership not yet started

**Decided: deny** — *not started*, with the start date shown, so reception can say "from Monday"
rather than investigate.

### 13.3 Membership frozen

**Decided: deny** — *frozen until {date}*, with the option for reception to end the freeze early on the
spot if the member wants to return. This is the common case: they came back sooner than planned.

### 13.4 Outside opening hours, and outside plan hours

**Decided: two different checks, both required.**

- **Plan hours** — an off-peak membership outside its window is denied even when the gym is open.
- **Gym hours** — outside opening hours, access is denied unless the plan grants out-of-hours access
  (24/7 plans and staffed-hours plans are different products at different prices).

**Decided:** hours are evaluated in the gym's local time, honouring the approved DST policy (AD-41).
An off-peak window ending at 16:00 means 16:00 on the wall clock all year.

### 13.5 Wrong location

**Decided: deny** unless the membership's location scope includes this location (§14).

### 13.6 Banned members

**Decided: always deny.** A ban exists for conduct or safety reasons, so it outranks a valid
membership and a paid balance.

- A ban has a reason, a start, an optional end, and the staff member who applied it.
- **Decided:** only a manager-level permission may apply, lift, or override a ban. A receptionist
  under pressure from the banned person must not be able to let them in.
- **Decided:** a ban does not automatically cancel the membership or stop billing. Those are separate
  commercial decisions, and coupling them means a safety action silently triggers a refund dispute.

### 13.7 Outstanding balance and failed payment

**Default: warn, do not block** — and this is a deliberate operational stance.

> **Why.** The door is the worst place to have a payment conversation. It is public, the member is
> often mid-routine, and reception cannot resolve a card failure in the thirty seconds before the queue
> builds. A flag at the desk lets staff raise it at a sensible moment; a locked door creates a
> confrontation and, often, a cancellation.

**Decided:** a **hard block is available** for gyms that want it, configured by an amount threshold
and a number of days overdue — because some gyms genuinely need it, and pretending otherwise leaves
them managing debt on paper.

Whether the default is warn or block, and the thresholds, are OPEN — §20.3.

### 13.8 Staff override

**Decided: yes, always available, for any denial except a ban.**

Reception must be able to admit someone the system refused. The alternative is a member turned away
because of a data error, which costs more than any override ever will.

- Requires a permission, a **reason** (from a short list plus free text), and is audited.
- Creates a visit marked as an override.
- **Decided:** overrides do **not** consume entitlement unless the operator says they should — most
  overrides exist because the entitlement calculation was the thing that was wrong.
- **Decided:** overrides are reported. A member overridden weekly is a data problem, and the owner
  should see it as one.
- A **ban** requires a manager, not a receptionist (§13.6).

### 13.9 What a denial must never do

**Decided:** a denial never deletes, alters, or consumes anything. It records the attempt and its
reason. A member denied five times has five recorded attempts and unchanged entitlement.

### 13.10 Emergency mode

**Decided: required.** Fire, medical emergency, power failure, evacuation.

- **Egress is never blocked by GymOS.** Exit is a life-safety function and is not gated on a
  membership. Where physical egress is controlled by hardware, that hardware must fail open. This is a
  legal and safety requirement, not a product preference.
- **Entry emergency mode** admits everyone, for a **time-boxed** period, activated by a permission,
  with a reason.
- Every entry during emergency mode is recorded and flagged.
- **Decided:** emergency mode expires automatically. A mode that stays on until someone remembers is
  an unlocked building.
- **Decided:** activating it is a prominent, audited event, and it is visible while active. An owner
  must be able to see that the door has been open all afternoon.

---

## 14. Multi-location and multi-gym

**Decided — three distinct situations, frequently confused:**

| Situation | Model | Shared member? | Shared membership? |
|---|---|---|---|
| **One gym, several locations** (branches of one business) | One tenant, several locations | **Yes** — one member record | **Yes**, subject to the plan's location scope |
| **A franchise** (separate businesses, one brand) | Separate tenants | **No** | **No**, without an explicit franchise agreement |
| **Unrelated gyms** | Separate tenants | **No** | **No** |

### 14.1 One membership across several locations

**Decided: yes, within one gym**, governed by the plan's **location scope**:

- **Home location only** — cheapest, and the common default.
- **Named locations** — a subset.
- **All locations** — a premium product.

The access decision consults this scope (§13.5, step 4).

### 14.2 One member across several locations

**Decided: yes — one member record, one history, one credential**, with a **home location** for
reporting and ownership.

Duplicating a member per branch is the same error as duplicating a returning member, at scale.

### 14.3 Transfers between locations

**Decided: supported as a change of home location.** The membership continues; nothing is cancelled or
re-sold. Recorded, so "which branch does this member belong to?" has a dated answer.

### 14.4 Franchise sharing

**Default: not supported in the MVP.**

Cross-tenant access requires answering who holds the contract, who receives the revenue for a visit at
another franchisee's site, who is the data controller for the member's record, and what one franchisee
may see of another's members. These are commercial and legal questions, not technical ones, and the
frozen architecture deliberately keeps tenants isolated.

The approved architecture already provides the mechanism for later cross-tenant arrangements —
versioned contracts between services rather than a shared database. OPEN — §20.15.

### 14.5 Shared profiles across gyms

**Default: no.** Two unrelated gyms holding one shared member profile means each can see data the
other collected, under a consent neither obtained. It is a data-protection problem before it is a
feature.

A person is free to be a member of both; the records are simply separate.

---

## 15. The member lifecycle

**Decided — and this is the most important structural decision in the document: there are two
lifecycles, not one.** A member's status and a membership's status are different things that change
for different reasons.

Collapsing them causes concrete problems: a member whose membership expired is *not* an expired
person — they are a former member the owner wants to win back, and their record must stay usable. A
frozen membership does not freeze the human being.

### 15.1 Member lifecycle

```
   Prospect ──────► Active ──────► Inactive ──────► Archived
      │                │               │                │
      │                ▼               ▼                │
      └──────────►  Banned ◄───────────┘                │
                       │                                 │
                       └───────────► (Erased) ◄──────────┘
```

| Status | Meaning | Entered when |
|---|---|---|
| **Prospect** | Known to the gym, never had a membership | Record created from a tour, enquiry, or lead conversion |
| **Active** | Holds a membership in force | A membership starts |
| **Inactive** | Had a membership; none in force now | Last membership ends (expiry or cancellation) |
| **Banned** | Refused entry regardless of membership | Staff action (§13.6) |
| **Archived** | Retained for records; not in day-to-day lists | Staff action, or automatically after a long period inactive |
| **Erased** | Personal data removed under GDPR; a non-identifying shell remains for financial and audit integrity | Approved erasure request |

**Decided:** *Active* and *Inactive* are **derived** from memberships, not set by hand. Reception must
never have to remember to mark someone inactive — they will not, and the member count will be wrong.

*Banned*, *Archived*, and *Erased* are **explicit** staff actions, because each is a decision.

**Transitions:**

| From → To | Trigger | Notes |
|---|---|---|
| Prospect → Active | First membership starts | |
| Prospect → Archived | Staff action, or long inactivity | A lead that never converted |
| Active → Inactive | Last membership ends | Automatic |
| Inactive → Active | A new membership starts | The returning member — same record (§4.8) |
| Active / Inactive → Banned | Staff action, manager permission | Membership and billing unaffected (§13.6) |
| Banned → previous | Ban lifted or expires | Returns to whatever their memberships imply |
| Inactive → Archived | Staff action, or automatic after a long period | Retention-driven |
| Archived → Inactive/Active | Return (§4.8) | Un-archived, archival kept in history |
| Any → Erased | Approved erasure | Terminal. Not reversible. |

**Decided:** *Lead* and *Invited* from the example lifecycle belong to **CRM**, not here. A lead is
not yet a member; conversion creates the member record as a *Prospect*. Keeping leads out of the member
list is what keeps the member count meaningful.

### 15.2 Membership lifecycle

```
   Pending ──────► Active ──────► Expired
      │              │  ▲             │
      │              │  │             │
      │              ▼  │             │
      │            Frozen             │
      │              │                │
      ▼              ▼                ▼
   Cancelled ◄───────┴────────────────┘
```

| Status | Meaning |
|---|---|
| **Pending** | Agreed, future-dated, grants nothing yet |
| **Active** | In force; grants entitlement |
| **Frozen** | Temporarily suspended by agreement; grants nothing |
| **Expired** | Reached its end date without renewal |
| **Cancelled** | Ended by the member or the gym before its natural end |

**Transitions:**

| From → To | Trigger | Notes |
|---|---|---|
| Pending → Active | Start date reached | Derived, not a job (§6.8) |
| Pending → Cancelled | Cancelled before it starts | No period, no charge |
| Active → Frozen | Freeze begins | Within plan limits (§6.7) |
| Frozen → Active | Freeze ends, or is ended early | Reception can end it at the door (§13.3) |
| Active → Expired | End date reached without renewal | Derived |
| Active → Cancelled | Cancellation effective | Immediate or at period end (§10.1) |
| Frozen → Cancelled | Cancelled while frozen | Permitted; members do leave mid-freeze |
| Expired → Active | Renewal | New period on the same membership (§8) |
| Cancelled → Active | Reinstated before period end | §10.1 |

**Decided:** *Expired* and *Cancelled* are distinguished permanently. "Did they leave, or did they
just lapse?" is the difference between a churn problem and an admin problem, and the owner needs both
numbers.

---

## 16. Business rules

Every rule is stated so it can be tested. Codes follow the platform convention.

### 16.1 Member

| Code | Rule |
|---|---|
| `members.name_required` | A member has a first and last name. |
| `members.one_contact_method_required` | At least one of email or mobile number. |
| `members.email_unique_per_gym` | An email address identifies at most one member within a gym. Not globally — the same person may belong to two gyms. |
| `members.mobile_unique_per_gym` | Default; a gym may permit sharing (a family with one phone). |
| `members.date_of_birth_not_future` | And not implausibly distant. |
| `members.date_of_birth_required_for_membership` | Required before a membership is sold. |
| `members.minor_requires_guardian` | A minor has a guardian relationship and recorded consent before a membership or check-in. |
| `members.emergency_contact_before_first_check_in` | Default; configurable. |
| `members.waiver_before_first_check_in` | |
| `members.belongs_to_exactly_one_gym` | Immutable. |
| `members.returning_member_reuses_record` | A returning person is a new membership, never a new member. |
| `members.duplicate_detection_warns_never_blocks` | |
| `members.merge_requires_permission_and_reason` | And is audited, previewed, and leaves a redirect. |
| `members.archive_does_not_delete` | |
| `members.erasure_preserves_financial_and_audit_integrity` | Erasure removes identifying data; financial records and the audit trail survive in non-identifying form. |
| `members.health_data_separately_permissioned` | Never on a list, never in a log, own retention. |
| `members.staff_and_member_are_linked_not_merged` | |

### 16.2 Membership

| Code | Rule |
|---|---|
| `memberships.one_active_primary` | At most one primary membership in force at any instant. |
| `memberships.primaries_never_overlap` | Including a queued future one — adjacent or with a gap, never overlapping. |
| `memberships.add_ons_may_overlap` | Freely, with each other and with a primary. |
| `memberships.add_on_requires_primary` | Default; a plan may permit standalone (a PT-only client who never uses the gym floor). |
| `memberships.end_after_start` | |
| `memberships.at_most_one_queued_primary` | Default. |
| `memberships.backdating_requires_permission_and_reason` | And never retroactively creates or removes visits. |
| `memberships.price_snapshot_at_agreement` | A plan price change never re-prices an existing membership. |
| `memberships.freeze_within_period` | A freeze cannot extend beyond the period it freezes. |
| `memberships.freeze_within_plan_limits` | Count per year, days per year, minimum duration. |
| `memberships.frozen_grants_no_access` | |
| `memberships.freeze_may_end_early` | |
| `memberships.cancellation_never_retroactive` | |
| `memberships.cancellation_requires_reason` | Categorized plus free text. |
| `memberships.cancellation_reversible_before_period_end` | |
| `memberships.renewal_extends_same_membership` | Tenure is continuous. |
| `memberships.upgrade_immediate_by_default` | |
| `memberships.downgrade_at_period_end_by_default` | |
| `memberships.consumed_entitlement_not_returned_on_change` | |
| `memberships.not_transferable_between_people` | Default; where permitted, it is a cancellation plus a new membership. |
| `memberships.expiry_is_derived_not_scheduled` | Never a status a job switches. |
| `memberships.entitlement_never_negative` | Refused, not clamped. |
| `memberships.unlimited_plans_still_record_visits` | |
| `memberships.location_scope_declared` | Every plan states which locations it admits. |

### 16.3 Attendance

| Code | Rule |
|---|---|
| `attendance.every_attempt_recorded` | Including denials, with a reason. |
| `attendance.duplicate_scan_within_window_is_one_check_in` | Default 90 seconds, same credential, same device. |
| `attendance.same_day_re_entry_joins_existing_visit` | One visit per member per location per local day (default). |
| `attendance.visit_consumes_entitlement_once` | Never twice for one visit. |
| `attendance.visit_requires_allowed_check_in` | No allowed check-in, no visit. |
| `attendance.check_in_immutable` | Corrected by a compensating record, never edited. |
| `attendance.correction_requires_permission_and_reason` | |
| `attendance.correction_adjusts_entitlement` | Or the member's remaining visits stay wrong. |
| `attendance.manual_check_in_runs_the_access_decision` | It is not an exception to the rules; overriding a denial is a separate action. |
| `attendance.manual_check_in_consumes_entitlement` | |
| `attendance.imported_attendance_does_not_consume_entitlement` | |
| `attendance.imported_attendance_marked_with_source` | |
| `attendance.offline_check_in_not_duplicated_on_sync` | |
| `attendance.gateway_time_annotated_never_rewritten` | Reported time plus measured skew plus received-at. |
| `attendance.offline_conflict_surfaced_not_resolved_silently` | |
| `attendance.occupancy_is_an_estimate_without_check_out` | And is labelled as one. |
| `attendance.retention_period_enforced` | |

### 16.4 Access

| Code | Rule |
|---|---|
| `access.evaluated_at_moment_of_presentation` | From dates and state, never from a job-maintained flag. |
| `access.first_failing_rule_decides` | In the order at §13. |
| `access.denial_carries_actionable_reason` | Member-facing short form and staff-facing full reason. |
| `access.ban_outranks_everything_except_emergency_mode` | |
| `access.ban_requires_manager_to_apply_lift_or_override` | |
| `access.ban_does_not_alter_membership_or_billing` | |
| `access.expired_within_grace_allowed_and_flagged` | |
| `access.frozen_denied_with_end_date` | |
| `access.pending_denied_with_start_date` | |
| `access.plan_hours_and_gym_hours_both_evaluated` | |
| `access.hours_evaluated_in_gym_local_time` | With the approved DST policy. |
| `access.location_must_be_in_membership_scope` | |
| `access.balance_warns_by_default` | Hard block available, configured by amount and days overdue. |
| `access.override_available_for_any_denial_except_ban` | With permission, reason, and audit. |
| `access.override_does_not_consume_entitlement_by_default` | |
| `access.overrides_are_reported` | Repeated overrides indicate a data problem. |
| `access.denial_changes_no_state` | |
| `access.egress_never_blocked` | Life safety. Hardware fails open. |
| `access.emergency_mode_is_time_boxed_and_audited` | Expires automatically; visible while active. |

---

## 17. Domain events

Named per the platform convention — `gymos.<module>.<aggregate>.<action>`, past tense.

### Members

| Event | Raised when |
|---|---|
| `gymos.members.member.registered` | A member record is created |
| `gymos.members.member.updated` | Identity or contact detail changes |
| `gymos.members.member.contact_details_changed` | Specifically contact detail — CRM cares about this alone |
| `gymos.members.member.photo_attached` | |
| `gymos.members.member.banned` | |
| `gymos.members.member.ban_lifted` | |
| `gymos.members.member.archived` | |
| `gymos.members.member.unarchived` | A former member returns |
| `gymos.members.member.erased` | Erasure completed |
| `gymos.members.member.merged` | Two records merged; carries surviving and absorbed |
| `gymos.members.relationship.added` | Guardian, family, corporate, payer, emergency contact |
| `gymos.members.relationship.removed` | |
| `gymos.members.guardian_consent.recorded` | |
| `gymos.members.waiver.signed` | |
| `gymos.members.health_declaration.recorded` | Carries no health detail in the payload |
| `gymos.members.duplicate.suspected` | Detection fired; feeds a data-quality queue |

### Memberships

| Event | Raised when |
|---|---|
| `gymos.memberships.membership.agreed` | Sold — including future-dated, before it starts |
| `gymos.memberships.membership.started` | Became active |
| `gymos.memberships.membership.renewed` | New period on the same membership |
| `gymos.memberships.membership.plan_changed` | Upgrade or downgrade; carries direction |
| `gymos.memberships.membership.frozen` | |
| `gymos.memberships.membership.unfrozen` | Including ended early |
| `gymos.memberships.membership.expired` | Reached its end date |
| `gymos.memberships.membership.cancelled` | Carries reason and effective date |
| `gymos.memberships.membership.reinstated` | Cancellation reversed |
| `gymos.memberships.membership.expiring_soon` | Threshold reached — drives reminders |
| `gymos.memberships.membership.location_scope_changed` | |
| `gymos.memberships.membership.home_location_changed` | Transfer between locations |
| `gymos.memberships.entitlement.consumed` | A visit or credit spent |
| `gymos.memberships.entitlement.restored` | A correction returned it |
| `gymos.memberships.entitlement.exhausted` | Ran out — drives an upsell prompt |
| `gymos.memberships.auto_renewal.enabled` | |
| `gymos.memberships.auto_renewal.disabled` | An early churn signal worth its own event |
| `gymos.memberships.renewal.failed` | Charge failed; drives dunning |

### Attendance and access

| Event | Raised when |
|---|---|
| `gymos.attendance.member.checked_in` | Entry allowed |
| `gymos.attendance.member.checked_out` | Where check-out exists |
| `gymos.attendance.check_in.denied` | Carries the reason code |
| `gymos.attendance.visit.started` | A new visit opened |
| `gymos.attendance.visit.closed` | Check-out or end of day |
| `gymos.attendance.visit.corrected` | Added or voided, with reason |
| `gymos.attendance.check_in.overridden` | Staff admitted someone the decision denied |
| `gymos.attendance.attendance.imported` | A migration batch completed |
| `gymos.attendance.offline_batch.synchronized` | A gateway reconnected and uploaded |
| `gymos.attendance.offline_conflict.detected` | A late check-in conflicts with later state |
| `gymos.access.emergency_mode.activated` | Carries reason and expiry |
| `gymos.access.emergency_mode.deactivated` | |
| `gymos.access.occupancy.threshold_reached` | Capacity warning |

**Decided:** `member.checked_in` and `visit.started` are separate events. A re-entry produces a
check-in without a visit, and consumers care about different ones: occupancy follows check-ins,
retention analytics follow visits.

---

## 18. What is deliberately out of scope

Stated so that implementation does not drift into it, and so nobody assumes it was forgotten.

| Out of scope | Where it belongs |
|---|---|
| Class booking, waitlists, timetables | Scheduling (already designed, AD-25) |
| Payment processing, invoicing, dunning mechanics | Payments |
| Leads before conversion, campaigns, consent | CRM |
| Workout programmes, PT session scheduling | Trainer |
| Retail sales, till operations | POS |
| Reporting and analytics surfaces | Reports |
| Physical hardware, readers, HAL plug-ins | Gateway (AD-21, AD-24) |
| Biometric identification (face, fingerprint) | Not in scope. Special-category data requiring a DPIA and an explicit legal basis. **Recommend excluding until counsel advises.** |

---

## 19. Guardrails for implementation

The decisions most likely to be quietly undone by an implementation that means well.

1. **Do not add a status column that a job maintains.** Member *Active*/*Inactive* and membership
   *Active*/*Expired* are derived. A job that flips them will one day not run.
2. **Do not store an entitlement balance as the source of truth.** Derive it from what was granted and
   what was consumed. A stored balance and its ledger will disagree, and a member will find out first.
3. **Do not let a denied check-in go unrecorded.**
4. **Do not merge staff and member records.**
5. **Do not create a second member record for a returning person.**
6. **Do not block registration on duplicate detection.**
7. **Do not make health data reachable from a list, a log, or a check-in screen.**
8. **Do not gate egress on anything.**
9. **Do not resolve an offline conflict silently.**
10. **Do not treat "unlimited" as "do not record visits".**
11. **Do not evaluate hours in UTC.** The gym's local day and wall clock decide.
12. **Do not implement anything marked OPEN below** before it is answered.

---

## 20. Open Product Decisions

Each requires an answer from the product owner before the affected work begins. **Blocking** means M2
implementation of that area cannot start without it.

---

### 20.1 Freezes: does frozen time extend the membership, and is a freeze billed? — **BLOCKING**

**Why it matters.** Directly changes revenue per member and what the member believes they bought. It
is also the single most common source of member disputes in gym software, and it cannot be changed
later without re-computing existing memberships.

**Options.**

| Option | Effect |
|---|---|
| **A. Extend, free** | End date moves by the frozen days; nothing charged. Most generous; the member paid for *time*. |
| **B. Extend, hold fee** | End date moves; a reduced fee is charged per frozen month. Industry-common — covers fixed costs and discourages indefinite freezing. |
| **C. Forfeit, free** | End date unchanged; the member loses the time. Simplest to compute; feels punitive and generates disputes. |
| **D. Per plan** | Each plan declares its own behaviour. Most flexible; most explaining at the desk. |

**Trade-offs.** A extends every membership's life and defers revenue. B protects revenue and is what
most chains do, but adds a charge the member must be told about clearly. C is the cheapest to build
and the most likely to cause arguments at reception — which is the cost that matters here. D multiplies
the cases staff must explain and members must understand.

**Recommendation: B, with the hold fee configurable to zero** — which yields A for gyms that prefer it,
without a second model. Combined with a plan-level cap on freeze count and total days.

---

### 20.2 What counts as one visit? — **BLOCKING**

**Why it matters.** On any visit-limited or credit-based plan, this *is* money, and members will
notice immediately if it is wrong. It also decides what "attendance" means in every report.

**Options.**

| Option | Effect |
|---|---|
| **A. One visit per local day per location** | Leaving for lunch and returning is one visit. |
| **B. One visit per entry** | Each entry consumes entitlement. |
| **C. One visit per configurable window** (e.g. 4 hours) | A middle ground. |
| **D. Per plan** | |

**Trade-offs.** B is literal and indefensible at the desk — a member who forgot their water bottle
should not lose a visit. A is what members assume and what staff can explain in one sentence; its edge
case is a 24-hour gym at midnight. C handles the midnight case but requires explaining a window nobody
asked about.

**Recommendation: A**, with the local-day boundary and a documented note that a 24-hour gym sees a
midnight boundary. Revisit only if a gym complains.

---

### 20.3 Grace periods and financial consequences at the door — **BLOCKING**

**Why it matters.** Decides whether members are turned away at the door for money reasons, which is the
highest-friction event in gym operations and a direct cause of cancellations.

**Sub-decisions.**

1. **Access grace after expiry** — length. Recommendation: **3 days**, admitted and flagged.
2. **Payment grace after a failed charge** — length before access is affected. Recommendation:
   **7 days**, warned at the desk throughout.
3. **Outstanding balance: warn or block by default?** Recommendation: **warn**, with a hard block
   available, off by default.
4. **Hard block thresholds** — amount and days overdue, when enabled. Recommendation:
   **configurable, no default**, so enabling it is a deliberate act.

**Trade-offs.** Longer grace means more service given away and a slower dunning cycle; shorter grace
means more confrontations at reception and more staff time spent on payment conversations at exactly
the wrong moment. The recommendation deliberately favours the front desk.

---

### 20.4 Proration on an immediate upgrade — **BLOCKING for upgrades**

**Why it matters.** Determines what the member is charged mid-period and whether the figure can be
explained to them.

**Options.** (A) Charge the full new price now, next period aligned. (B) Charge the difference
prorated by remaining days. (C) No charge until the next period; upgrade granted immediately.

**Trade-offs.** A over-charges and will be disputed. B is fair and is what members expect, but the
figure must be explainable at the desk — "€14.20 for the remaining 11 days" is fine; an unexplained
€14.20 is a complaint. C is generous, simple, and gives away days.

**Recommendation: B**, with the calculation shown to the member before they confirm.

---

### 20.5 Is health information collected in the MVP? — **BLOCKING**

**Why it matters.** Special-category data under GDPR Article 9. It needs an explicit lawful basis
(likely explicit consent), stricter access control, its own retention rule, and possibly a DPIA. Once
collected, it cannot be un-collected. It also shapes what the member sign-up flow asks for.

**Options.** (A) Collect nothing; operational "see notes" flag only. (B) A PAR-Q style declaration
with explicit consent. (C) Full medical notes.

**Trade-offs.** A ships fastest and carries the least risk, and the operational flag covers most of
what reception needs. B is what many gyms expect and may be needed for insurance, but requires the
consent wording, the retention rule, and the access model to be settled first. C is a liability
without a clear operational benefit at this stage.

**Recommendation: A for the MVP**, with the operational flag, and B designed properly as a follow-up
once the legal basis is documented. **Requires legal counsel before any option is implemented.**

---

### 20.6 Minors: age threshold, consent evidence, activity limits — **BLOCKING for junior memberships**

**Why it matters.** Legal compliance and safeguarding. Determines who may sign, what is retained as
evidence, and which activities have their own minimum ages.

**Questions.** Age of majority for contracting in Cyprus; what constitutes acceptable evidence of
guardian consent (signature, recorded acceptance, identity check); minimum ages per activity (gym
floor, free weights, classes, pool); whether a minor may check in unaccompanied; whether a guardian
must be present on site.

**Recommendation.** Do not guess any part of this. **Obtain written guidance from legal counsel**, then
implement exactly. Until then, junior memberships stay out of scope.

---

### 20.7 Multi-location: in scope for M2? — **BLOCKING for scope**

**Why it matters.** Location scope touches memberships, access, reporting, and staff permissions.
Retrofitting a location dimension after single-location data exists is a data migration; building it
speculatively costs M2 time it may not have.

**Options.** (A) Single location in M2; locations later. (B) Model location from the start, with most
gyms having exactly one. (C) Full multi-location including cross-location access rules.

**Trade-offs.** A is fastest and risks a painful migration. B costs modest extra design now and makes
the later step additive — every gym has one location, and the concept simply exists. C is more than M2
needs.

**Recommendation: B.** Model the concept, ship with one location per gym, defer cross-location pricing
and transfer workflows. This is the cheap insurance.

---

### 20.8 Family and corporate memberships: in scope? — Non-blocking for core

**Why it matters.** Both change who pays, who is entitled, and what happens when the group changes
(a child turns 18, an employee leaves). Both are commonly requested and commonly under-specified.

**Questions.** Is the family a pricing construct or an entitlement pool? What happens when a member
leaves the group mid-period? Who is billed? Does the corporate agreement verify employment, and how?

**Recommendation.** Out of the M2 core. Design them as a follow-up once single-member memberships are
proven, and keep the *payer* relationship (§5) in the core model so that adding them later does not
require re-modelling who pays.

---

### 20.9 Guest passes and day passes: in scope? — Non-blocking

**Why it matters.** A day-pass visitor is a member with no ongoing membership — the model already
supports it — but guest *allowances* attached to a plan are additional entitlement machinery.

**Recommendation.** Support the **day pass** in M2 (a member plus a single-visit purchase; the model
already allows it, and reception needs it from day one). Defer plan-attached **guest allowances**.

---

### 20.10 Trials: length, eligibility, enforcement — Non-blocking

**Why it matters.** Trials are a primary acquisition tool, and their abuse (repeat trials under
different emails) is a real revenue leak that duplicate detection only partly addresses.

**Questions.** Length; one per person ever, or per period; what identity signal enforces it; does a
trial require payment details; what happens when it lapses without conversion.

**Recommendation.** Design as a normal short primary membership on a Trial plan, with a rule that a
person may hold at most one trial ever, enforced by the duplicate-detection signals. Confirm the
specifics with the owner before building.

---

### 20.11 Merging: who may merge, and is it reversible? — Non-blocking, needed before go-live

**Why it matters.** Merging moves attendance, memberships, and payments between records. Done wrongly
it corrupts financial history; made too hard, staff work around it and duplicates accumulate.

**Questions.** Which permission? Reversible, or forward-only with a redirect? What happens when both
records have active memberships, or conflicting payment methods? Can merge be undone after a payment
has been taken against the survivor?

**Recommendation.** Manager-level permission; **forward-only with a permanent redirect** (reversal is
where the corruption risk lives); a mandatory preview showing exactly what moves; refuse the merge
outright when both records hold an active primary membership, and require one to be ended first.

---

### 20.12 Cancellation terms: notice, refunds, early termination — **BLOCKING for cancellation**

**Why it matters.** Contractual and consumer-law territory, and it decides what reception is allowed to
promise.

**Questions.** Notice period; is the current period always payable; are refunds ever given, and by
whose authority; early-termination fees on a committed term; cooling-off rights under Cyprus and EU
consumer law for distance sales.

**Recommendation.** Answer with the owner and **confirm the consumer-law position with legal counsel**,
particularly cooling-off rights for memberships sold online. Default to cancellation effective at
period end with no refund, since that is the simplest defensible position — but confirm it.

---

### 20.13 Minimum commitment terms — Non-blocking for M2

**Why it matters.** A 12-month committed membership behaves differently from a rolling monthly one:
cancellation, freezing, and upgrades all interact with the commitment, and the price usually depends on
it.

**Recommendation.** Keep out of the M2 core, but confirm early whether it is coming. If it is, the
membership model should not assume every agreement is rolling — that assumption is expensive to reverse.

---

### 20.14 Attendance retention period — **BLOCKING for go-live, not for build**

**Why it matters.** Attendance is a record of a person's movements. Keeping it indefinitely by default
is not defensible under GDPR data-minimisation.

**Questions.** How long are identifiable visit records kept? What is retained in aggregate afterwards?
Does an erasure request remove visit history, and are aggregates recomputed?

**Recommendation.** A defined period — **24 months** identifiable, then irreversible aggregation — as a
starting proposal. **Confirm with legal counsel**, since the answer interacts with financial-record
retention obligations.

---

### 20.15 Franchise sharing — Non-blocking, out of MVP

**Why it matters.** Cross-tenant access is a commercial and data-protection arrangement before it is a
feature: revenue attribution per visit, who is data controller, and what one franchisee sees of
another's members.

**Recommendation.** Explicitly out of the MVP. Revisit only with a named franchise customer and a
signed agreement describing the commercial terms, at which point the approved architecture's
cross-service contracts are the mechanism.

---

## 21. Summary of what must be answered before implementation

**Blocking M2:** §20.1 freezes · §20.2 visit definition · §20.3 grace and door-level financial
consequences · §20.4 upgrade proration · §20.5 health data · §20.7 multi-location scope · §20.12
cancellation terms. Plus §20.6 if junior memberships are in scope.

**Blocking go-live, not the build:** §20.14 attendance retention.

**Deferred by recommendation:** §20.8 family and corporate · §20.9 guest allowances · §20.10 trials ·
§20.11 merge policy (before go-live) · §20.13 committed terms · §20.15 franchise.

**Legal counsel required:** §20.5 health data · §20.6 minors · §20.12 consumer law and cooling-off ·
§20.14 retention. Biometric identification remains out of scope pending advice (§18).

---

**No implementation may begin on a blocking item until it is resolved and this document is updated to
record the decision.**
