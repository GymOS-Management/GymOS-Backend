# Members Domain — Business Design

**Status: FROZEN — approved 2026-08-05.** Authoritative business specification for the Members,
Memberships, Attendance, and Access domain. **M2 implementation may begin.** Implementation must
follow this document exactly.
**Change protocol:** frozen means a change requires a **superseding decision recorded in the
[Decision Register](#20-decision-register)** before implementation follows it — the same discipline the
architecture uses for ADRs. Do not silently reinterpret a Decided statement.
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
| **Decided** | Settled — by the frozen architecture, by an approved product decision (§20), or because operational reality admits one sensible answer. Implement as written. |
| **Configurable** | A per-gym setting. The stated value is the shipped default; the gym may change it. Implement both the behaviour and the setting. |
| **DEFERRED — §20.n** | Explicitly not in scope now, with a recorded reason. **Do not implement.** Either awaiting legal review or scheduled after M2. |

All product decisions that blocked implementation have been **approved and resolved**. What remains
DEFERRED is either (a) awaiting legal review, where a wrong guess is a compliance exposure rather than
a preference, or (b) a feature deliberately scheduled after M2. Both are listed in §20.

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

Most of the difficulty in this domain comes from collapsing concepts that behave differently. Six must
stay distinct.

**Person** — a human being. Not a stored thing in GymOS today; the word is used here to talk about
identity across gyms and across roles.

**Member** — a person's *relationship with one gym*. It is the record that persists: their name,
their history, their notes, their attendance. A member exists whether or not they are currently
paying, and continues to exist after they leave.

**Membership Type** — a *product the gym sells*: "Monthly Unlimited", "Off-Peak", "10-Visit Pass",
"Junior", "Staff". It defines price, duration, entitlements, and restrictions. It is a template, and it
is never a member's possession.

**Membership Instance** — a member's *actual agreement*, created from a Membership Type: this member,
these dates, this agreed price, this remaining entitlement, this freeze history. It starts, may be
frozen, and ends. A member may have had a dozen instances over eight years.

> **Why the two are separate (approved decision D11).** Changing the price of "Monthly Unlimited"
> must not re-price four hundred existing members. Retiring a Membership Type must not delete the
> history of everyone who ever held it. An instance therefore carries its own snapshot of what was
> agreed, and the Type is free to change without rewriting the past.

**Member Number** — a short, permanent, human-readable identifier the member and staff actually use
(§4.3). Distinct from any internal identifier, and never reused.

**Visit** — a *business fact*: this member attended on this day at this location. What entitlement is
consumed against and what retention reports count.

**Check-in** — a *technical event*: a credential was presented at a device at an instant, and was
allowed or denied. Several check-ins can belong to one visit. A denied check-in belongs to no visit
but must still be recorded.

> **Why this matters at the desk.** A member says "the door won't let me in." Reception needs to see
> the *denied check-in* with its reason within one click. If denials are not recorded, the only
> available answer is "try again", and the member is standing there.

**Terminology is binding.** This document uses *Membership Type* for the product and *Membership
Instance* for the agreement. Implementation must not blur them into one word, because the two have
different lifecycles, different permissions, and different audit meaning.

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

### 4.3 Permanent identity and the Member Number

**Decided (approved D9, D10).** A member has a **permanent identity that outlives every
membership**. The record is created once and persists through joining, lapsing, returning, freezing,
and archiving. Nothing in the member's commercial history can cause a second record to exist for the
same person at the same gym.

**Every member receives a GymOS Member Number on creation.** It is:

| Property | Decision |
|---|---|
| **Permanent** | Assigned once, at creation, and never changes — not on renewal, not on a change of Membership Type, not on returning after five years, not on transfer between locations. |
| **Never reused** | Not after archival, not after erasure, not after a merge. A retired number resolves to its history and to nothing else. |
| **Human-readable and speakable** | Short enough to read from a card, quote over the phone, and type at a busy desk. |
| **Unique within the gym** | Scoped per gym, matching the tenancy model. Two gyms may independently issue the same number, and that is not a collision — they are separate businesses. |
| **Searchable as a first-class identifier** | Reception must be able to find a member by number as directly as by name. |
| **Immutable by staff** | No permission edits it. A wrong number is not a thing that happens, because nothing derives it from mutable data. |
| **Assigned automatically** | Never typed by staff. A number reception has to invent is a number that will collide. |

> **Why it earns its place.** A member at the desk does not know their internal identifier and never
> will. They know the number on their card, or they know their surname. Staff need one field that finds
> a person unambiguously when three members share a surname — and the alternative, quoting a long
> opaque identifier down the phone, does not survive contact with a real front desk.

**Decided — on merge (§4.11):** the surviving record keeps its own Member Number. The absorbed number
is **permanently retired** and redirects to the survivor, so an old membership card, an old export, and
an old printed receipt all still resolve. It is never re-issued to anyone.

**Decided — on erasure:** the number and its retirement survive erasure as a non-identifying token, so
financial and audit records remain internally consistent. A retired number does not identify a person.

Whether the number carries a location or year component, and its exact presentation format, is
DEFERRED — §20.11. The properties above are frozen; the formatting is cosmetic and does not block
implementation.

### 4.4 What information is mandatory

**Decided.** The minimum to create a member is deliberately three things:

1. **First name**
2. **Last name**
3. **One means of contact** — email *or* mobile number. Either satisfies it.

Nothing else is required to create the record.

> **Why so little.** Every additional mandatory field at the front desk is a field that gets filled
> with `x`, `test`, or `01/01/1900` during a busy shift. Mandatory data that is routinely faked is
> worse than optional data that is sometimes missing, because nobody can tell which values are real.
> Data becomes required at the point where it is genuinely needed — see §4.6.

### 4.5 What information is optional

**Decided.** Everything else, including: preferred name, date of birth, gender (self-described, free
text or decline), address, secondary phone, photo, occupation, marketing source ("how did you hear
about us"), preferred language, notes, and emergency contact.

### 4.6 Information required *conditionally*

**Decided.** Some data is not required to create a member but *is* required before a specific thing
can happen. This is how the system asks for data at the moment it matters instead of up front.

| Requirement | Becomes mandatory when |
|---|---|
| Email address | The member is given member-app access, or is sent anything by email. |
| Date of birth | A membership is sold (age-restricted Membership Types, and it is the primary duplicate-detection signal). |
| Guardian details and consent | The member is a minor — see §4.10. |
| Emergency contact | **Default:** before the first check-in. A gym is a place where people collapse. |
| Signed waiver / terms acceptance | Before the first check-in. |
| Payment method | A recurring membership is sold. |
| Medical attention flag | Never mandatory. Optional at all times (§4.13). |

### 4.7 Can a member belong to multiple gyms?

**Decided: a member record belongs to exactly one gym** (a tenant, per the frozen tenancy model).

A person who trains at two unrelated gyms is two member records. They are not linked, and neither gym
can see the other's record. This is correct: the two gyms are separate businesses and separate data
controllers.

"One gym with several locations" is a different question entirely and is answered in §14.

### 4.8 Can staff also be members? Can trainers?

**Decided: yes, and they must be modelled as two roles on one person, never as one merged record.**

Staff and trainers commonly train at the gym they work for, usually free. They need to be members
because they check in, book classes, and may buy personal training or retail.

- A staff account and a member record are **linked**, not merged.
- Their attendance is recorded like anyone's.
- **Default:** staff memberships are a normal membership on a "Staff" Membership Type priced at zero, so it
  expires with employment and appears in reports like any other.

> **Why not merge them.** Merging makes every question ambiguous: does the trainer appear in the
> member count the owner reports to their accountant? Does their check-in count towards occupancy?
> Do they receive member marketing? Two linked records answer each question separately.

**Decided:** a staff member's *employment* ending does not delete their member record, and their
membership ending does not affect their staff account. The two lifecycles are independent.

### 4.9 Can a former member return?

**Decided: yes, and the same member record is reused. Always.**

Creating a second record for a returning member is the most common and most damaging data error in gym
software. It loses their attendance history, their notes, their PT programme, their waiver, and it
inflates the member count the owner makes decisions with.

- Returning is a **new membership on the existing member**, not a new member.
- Duplicate detection (§4.11) exists primarily to catch this at the front desk.
- **Decided:** a member whose record was archived is *un-archived* on return, and the archival is kept
  in history rather than erased.

### 4.10 Can minors be members?

**Decided: yes.** Gyms have junior memberships, family memberships, and swimming lessons.

- A minor's record requires a **guardian relationship** and recorded **guardian consent** before a
  membership is sold or a check-in is permitted.
- The guardian may or may not themselves be a member. Both must be supported — a parent who signs up
  their child but does not train is common.
- **Default:** the guardian is the default recipient of the minor's communications and the default
  payer.
**Decided (approved D6): minors are supported from day one, and every age threshold is
configurable per gym.** Age policy is data, not code:

| Setting | Shipped default |
|---|---|
| Age of majority for contracting | **18** — the guardian signs below this |
| Minimum age for any membership | **Configurable, no default** — a deliberate choice per gym |
| Minimum age for unsupervised gym-floor access | **16** |
| Minimum age for unaccompanied check-in | **16** |
| Age at which a junior membership must convert | **Configurable; off by default** |
| Activity-specific minimum ages | **Configurable per activity, none by default** |

> **Why configurable rather than hard-coded.** The correct values are a legal and insurance question
> that differs by jurisdiction and by what the gym's policy actually permits. Making them settings means
> the legal answer, when it arrives, is a configuration change rather than a release — which is the
> whole point of asking the question before implementation.

**DEFERRED — §20.1 (legal review):** the *values* for Cyprus, and what constitutes acceptable evidence
of guardian consent (a signature, a recorded acceptance, an identity check). Build the mechanism and the
settings; **do not ship default values for a live gym until counsel has confirmed them.**

### 4.11 Can duplicates exist, and how is detection meant to work?

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
record leaves a permanent redirect so an old reference resolves, including its retired Member Number
(§4.3). Remaining merge policy details are DEFERRED — §20.9.

### 4.12 Emergency contacts

**Decided: yes, and at least one is expected before the first check-in** (Default; §4.6).

- A member may have several, in priority order.
- An emergency contact is **not** a member and needs no account — a name, a relationship, and a phone
  number.
- **Decided:** emergency contact details are visible to staff on duty without needing a permission
  that a receptionist would not have at 06:00. Locking away the phone number to call in an emergency
  is a safety failure dressed as data protection.
- A minor's guardian is automatically an emergency contact unless overridden.

### 4.13 Medical information

**Decided (approved D5): the MVP stores no detailed medical data.** Exactly three things, and nothing
more:

1. **Emergency contact information** (§4.12) — who to call.
2. **Optional internal notes** — free-text operational notes about the member. Not a medical record.
3. **An optional medical attention flag** — a single indicator telling staff that this member may need
   particular attention, carrying **no detail whatsoever**.

No conditions, no medications, no diagnoses, no health questionnaire answers, no injury history.

> **Why this is the right MVP.** Health data is special-category personal data under GDPR Article 9 and
> cannot be collected on the same basis as a phone number. These three things give reception and trainers
> what they operationally need — *ask this person before they start* — without the gym becoming the
> custodian of medical records it has no lawful basis to hold, no access model to protect, and no
> retention rule to expire.

**Decided — the flag and the notes are treated differently, because they are used differently.**

| | Medical attention flag | Internal notes |
|---|---|---|
| Contains | Nothing but itself — set or not set | Free text written by staff |
| Visible at check-in | **Yes.** This is its entire purpose: the desk learns to ask before the member starts training. A flag nobody sees at the moment of use is not a safety feature. | **No.** A note is read deliberately, on the member's record. |
| Visible in a list | **As an icon only**, never as a filter or a column of values | No |
| In exports, reports, marketing segments | **Never** | **Never** |
| In application logs | **Never** | **Never** |
| In audit values | **Redacted** | **Redacted** |
| Set or cleared by | Staff, deliberately. Never an automated rule. | Staff |
| Removed on member request | **Yes, always** | **Yes, always** |

**Decided:** a member may decline both without losing access to anything, except an activity that
legally requires a declaration — and no such activity exists in the MVP, because no declaration exists.

**Decided:** staff must be told **in the interface where notes are written**, not in a policy document
nobody reads, that notes are not the place for medical detail. A warning at the point of writing is the
only one that works.

**Decided:** the operational "see notes before check-in" indicator considered during design and the
approved medical attention flag are **the same thing**. One flag, not two.

**DEFERRED — §20.2 (legal review):** the consent wording shown to the member for the flag and for
notes, the retention period for both, and whether a structured health declaration (PAR-Q) may be added
later. **A structured health declaration must not be built** until counsel has confirmed the lawful
basis. The *scope* above is frozen; only the wording and retention remain open.

## 5. Relationships

**Decided.** A member may be related to other people and organizations, and these are distinct
relationship types rather than one generic link.

| Relationship | Meaning | Notes |
|---|---|---|
| **Guardian of / ward of** | Legal responsibility for a minor | Carries consent, default payer, default recipient of communications. Required for minors. |
| **Family group member** | Shares a family or household membership | A commercial relationship, not an address match. Scope DEFERRED — §20.5. |
| **Corporate member** | Employed by an organization holding a corporate agreement | Eligibility depends on continued employment. Scope DEFERRED — §20.5. |
| **Emergency contact** | Who to call | Not a member; no account. |
| **Referred by** | Who introduced them | Feeds referral rewards; marketing rather than operational. |
| **Payer** | Who pays, when not the member | Guardian, employer, or another member. Distinct from every relationship above. |
| **Linked staff account** | This member is also staff or a trainer | §4.8. |
| **Previous record (merged)** | This member absorbed another record | Permanent, so old references resolve. |

**Decided:** relationships are directional and independently revocable. A guardian ceasing to be
guardian does not remove them as emergency contact, and a family group ending does not delete anyone.

**Decided:** the *payer* relationship is deliberately separate from family and corporate. Real
arrangements do not follow the household — a grandparent pays for one grandchild's membership while
the family group contains four people. Deriving the payer from the family group produces wrong invoices.

---

## 6. Membership fundamentals

### 6.1 Membership Type and Membership Instance

**Decided (approved D11): these are two separate concepts and must never be collapsed.**

**A Membership Type is a product.** It belongs to the gym, not to any member. It declares price,
duration, entitlement family, location scope, permitted hours, activity scope, freeze limits, hold fee,
and age restrictions. It may be edited, retired, or superseded.

**A Membership Instance is an agreement.** It belongs to one member, was created from one Membership
Type, and carries its **own snapshot of what was agreed** — most importantly the price. It has dates, a
status, remaining entitlement, and a freeze history.

| | Membership Type | Membership Instance |
|---|---|---|
| Belongs to | The gym | One member |
| Answers | "What do we sell?" | "What does this member have?" |
| Changing it affects | Future sales only | One member |
| Ends by | Being retired | Expiring or being cancelled |
| Count | Tens | Thousands |

**Decided — consequences that follow, and are the reason for the separation:**

- **Editing a Type never alters an existing Instance.** Raising the price of "Monthly Unlimited" affects
  new sales and renewals, never the four hundred people already on it. An existing member's price
  changes only by an explicit, recorded action on their Instance.
- **Retiring a Type never deletes history.** A retired Type stops being sellable; every Instance ever
  created from it remains intact and readable.
- **A retired Type may still renew existing Instances** if the gym chooses — a grandfathered price is a
  deliberate retention tool, not an accident.
- **Reports distinguish them.** "How many people are on Monthly Unlimited?" counts Instances; "which
  products do we sell?" lists Types.

### 6.2 Primary versus add-on — the central distinction

**Decided.** Memberships are of two kinds, and nearly every "can memberships overlap?" question
resolves once they are separated.

**Primary Instance** — grants access to the gym. This is what the member means by "my membership".
**At most one is in force at any moment.**

**Add-on Instance** — grants something extra alongside a primary: a block of personal-training
sessions, a class pack, locker rental, a tanning or sauna package, a second-location upgrade.
**Several may be in force at once, and they may overlap freely.**

A Membership Type declares which of the two it produces.

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
the 1st; a member gives notice and signs a new Membership Type starting when the old one ends; a January
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

**Decided (approved D1): a freeze extends the membership, and may carry a configurable hold fee.**

| Rule | Decision |
|---|---|
| Effect on the end date | **Extends it by the frozen duration.** The member paid for time, not for a calendar range. |
| Hold fee | **Configurable per Membership Type. Default €0** — free freezing unless the gym decides otherwise. |
| Extension is exact | The end date moves by the number of days actually frozen, so ending a freeze early extends by less. |
| Freeze ended early | Extension recalculated to the real duration. A member who returns after 9 days of a 30-day freeze gets 9 days, not 30. |
| Entitlement during a freeze | Not consumed and not forfeited. A visit-limited period resumes with what was left. |

**Configurable limits per Membership Type:** maximum freezes per year, maximum total frozen days per
year, minimum freeze duration (which stops a freeze being used to skip a single billing day), and
whether the member may self-serve a freeze or must ask staff.

> **Why extension rather than forfeit.** Forfeiting is cheaper to compute and generates an argument at
> reception every time it happens. Extension is what the member believes they bought, and the hold fee —
> when a gym chooses to charge one — covers the fixed cost of holding the place without the dispute.

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

## 7. Membership Types

**Decided.** A Membership Type defines what the member gets. Four families must be supported, and they combine.

| Family | Entitlement | Everyday example |
|---|---|---|
| **Time-based, unlimited** | Unlimited visits within a period | "Monthly unlimited" |
| **Time-based, limited** | A capped number of visits within a period, resetting each period | "8 visits per month" |
| **Visit-based (pack)** | A fixed number of visits, no period reset, usually with a long expiry | "10-visit pass, valid 6 months" |
| **Credit-based** | A balance of credits spent at varying rates | "20 credits; a class costs 2, a PT session 5" |

**Decided — modifiers that apply across families:**

- **Time restrictions (off-peak).** A Membership Type may permit access only within stated windows. Genuinely
  common and genuinely a discount lever: "off-peak" memberships are cheaper because they smooth demand.
  This means the access decision must consider the *Membership Type's* permitted hours, not only the gym's opening
  hours (§13.4).
- **Location scope.** Which locations the Type admits (§14).
- **Activity scope.** Gym floor only, or classes included, or pool included.
- **Guest allowance.** A number of guest visits per period.
- **Minimum commitment.** A contracted term. DEFERRED — §20.6.

**Decided:** an unlimited Membership Type still records visits. "Unlimited" describes entitlement, not
measurement — the owner's most valuable retention signal is attendance frequency, and it must not be
switched off by the Membership Type.

---

## 8. Renewal

**Decided.** A renewal produces a **new period on the same membership**, not a new membership. The
member's tenure with the gym is continuous, and reports about how long people stay depend on it.

### 8.1 Automatic renewal

**Default: available per Membership Type, and off unless the Membership Type says otherwise.**

- Auto-renewing memberships attempt a charge before the period ends, so a failure surfaces before the
  member is at the door.
- **Default:** the renewal charge is attempted several days ahead of the period end (setting), giving
  reception time to resolve a failure quietly.
- A member may cancel auto-renewal at any time; it stops the *next* renewal and does not end the
  current period.
- **Decided:** every renewal attempt and its outcome is recorded, whether it succeeded or not. "Why
  did my card get charged?" and "why didn't it?" are both frequent questions.

### 8.2 Manual renewal

**Decided.** Reception can renew at the desk in one action, defaulting to the same Membership Type and continuing
from the current period end so no gap is created by accident.

### 8.3 Renewing after a lapse

**Decided.** A member returning after their membership ended is renewed **from today** by default, not
backdated to close the gap. Backdating would charge them for time they did not have.

Whether a lapse resets tenure for loyalty purposes is a reporting decision, not an access one, and is
deferred.

---

## 9. Grace periods

**Decided.** Two distinct grace concepts exist and must not be conflated.

**Decided (approved D3): both are configurable per gym, with these shipped defaults.**

| Grace | Default | Meaning |
|---|---|---|
| **Membership access grace** | **3 days** after the period ends | Covers the everyday case of a renewal being a day or two late. |
| **Payment grace** | **7 days** after a failed charge | Time for reception to resolve a card failure away from the door. |

**Decided: warnings are the default; strict blocking is optional.** A gym may switch either grace to
zero and enable strict blocking, and some will. Neither is hard-coded, because a boutique studio and a
24-hour budget chain genuinely need different answers.

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

**Decided (approved D8): cancellation rules are configurable per gym.**

| Setting | Shipped default |
|---|---|
| Notice period | **0 days** — cancellation takes effect at period end |
| Cooling-off window after purchase | **Off** — see the legal note below |
| Refund on immediate cancellation | **None** — refunds are a deliberate staff action with a permission |
| Early-termination fee on a committed term | **Off** (committed terms are DEFERRED, §20.6) |
| Who may cancel | **Staff; member self-service off by default** |

**Decided — configuration has a floor.** A gym cannot configure away a statutory right. Where
consumer law grants a cooling-off period or a cancellation right, the configured value may be more
generous than the statutory minimum but never less. Implementation must treat statutory minimums as a
lower bound on the setting, not as another default.

**DEFERRED — §20.3 (legal review):** the statutory values for Cyprus and the EU, particularly
cooling-off rights for memberships sold online or off-premises, and whether they differ for a committed
term. **Ship the settings; do not ship default values for a live gym until counsel has confirmed the
statutory floor.**

### 10.2 Upgrade and downgrade

**Decided: both supported, and they are the same operation with different directions and timing
defaults.**

| | Default timing | Reasoning |
|---|---|---|
| **Upgrade** (more expensive or more entitlement) | **Immediate** | The member is paying more and wants it now. Delaying is a sale refused. |
| **Downgrade** (less expensive or less entitlement) | **At period end** | They have paid for the current period; downgrading immediately means a refund. |

**Decided:**

- A change of Membership Type continues the **same membership** with a new period, preserving tenure.
- The previous Type and price remain in history.
- Entitlement already consumed in the current period is **not** returned.
**Decided (approved D4): upgrades are automatically prorated, and the member sees the arithmetic
before confirming.**

1. The **unused value of the current period is credited** — the portion of what they paid that covers
   days not yet used.
2. That credit is applied against the new Membership Type's price for the remainder of the period.
3. The **resulting amount is shown to the member before they confirm**, itemized: what they paid, what
   is unused, what the new Type costs, what they owe today.
4. The next period bills at the new price in full.

**Decided:** a credit larger than the amount owed does **not** produce a refund by default. It carries
forward against the next period, and refunds remain a deliberate staff action (§10.1).

> **Why show the calculation.** "€14.20" with no explanation is a complaint; "you paid €40, 11 days
> unused is €14.67 credit, the new Type is €28.87 for those days, so €14.20 today" is a sale. The
> arithmetic is not the hard part — presenting it is what stops the dispute.

### 10.3 Transfer

**Decided — three different things are called "transfer", and only two are supported:**

| Kind | Supported | Notes |
|---|---|---|
| Transfer between **locations** of the same gym | **Yes** | Change of home location; membership continues. §14. |
| Transfer to a **different Membership Type** | **Yes** | That is an upgrade or downgrade, §10.2. |
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

**Re-entry window — Decided (approved D2): one visit equals one local calendar day per location.**
A further check-in on the same local calendar day at the same location joins the **existing visit**. It
does not create a second visit and does not consume entitlement twice. A check-in at a *different*
location on the same day is a separate visit, because it is separate attendance at a separate site.

> **Why the local calendar day, and why it matters.** Entitlement is money. On an "8 visits per month"
> Type, a member who steps out for lunch and returns must not be charged two of their eight visits —
> they will notice, and they will be right.
>
> **The boundary is the gym's local calendar day** (AD-41), which has one accepted consequence: in a
> 24-hour gym, a 23:50 entry and a 00:10 re-entry are two visits. This is accepted rather than
> engineered around. A rolling window would avoid it but would make "how many visits have I used this
> month?" unanswerable without explaining the window, and reception cannot explain a rolling window
> across a desk. A calendar day is what both staff and members already mean by "a day".

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
- Aggregated figures (visits per day, per Membership Type, per hour) may be kept longer, as they cease to identify
  anyone.
- A member exercising erasure removes their identifiable visits; aggregates already computed are not
  recomputed.

The retention period is **DEFERRED — §20.4 (legal review).** Blocks go-live, not the build.

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
| 6 | **Within the Membership Type's permitted hours?** | Deny — *outside Membership Type hours*. §13.4 |
| 7 | **Is the gym open, or does the Membership Type grant out-of-hours access?** | Deny — *gym closed*. §13.4 |
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

### 13.4 Outside opening hours, and outside Membership Type hours

**Decided: two different checks, both required.**

- **Type hours** — an off-peak membership outside its window is denied even when the gym is open.
- **Gym hours** — outside opening hours, access is denied unless the Membership Type grants out-of-hours access
  (24/7 and staffed-hours access are different products at different prices).

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

**Decided (approved D3):** **warn by default; strict blocking optional.** When strict blocking is
enabled, the gym configures the amount threshold and the days-overdue threshold; neither has a shipped
default, so enabling it is always a deliberate act with deliberate numbers.

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
| **One gym, several locations** (branches of one business) | One tenant, several locations | **Yes** — one member record | **Yes**, subject to the Membership Type's location scope |
| **A franchise** (separate businesses, one brand) | Separate tenants | **No** | **No**, without an explicit franchise agreement |
| **Unrelated gyms** | Separate tenants | **No** | **No** |

**Decided (approved D7): the data model supports multi-location from day one; a single location is
enabled by default.**

Every gym has at least one location and most will have exactly one. The concept exists everywhere it
belongs — members have a home location, Membership Types declare a location scope, visits record where
they happened — and the multi-location *workflows* (cross-location pricing, transfer between sites,
per-site reporting) stay switched off until a gym needs them.

> **Why model it now.** Adding a location dimension to members, memberships, and attendance after a
> gym has two years of single-site data is a data migration under load. Modelling it while every gym
> has one location costs a little design and nothing at runtime. This is the cheap insurance, taken
> deliberately.

**Decided:** with a single location enabled, none of it is visible to the gym. Reception never picks a
location, and no screen asks a question that has one possible answer.

### 14.1 One membership across several locations

**Decided: yes, within one gym**, governed by the Membership Type's **location scope**:

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
versioned contracts between services rather than a shared database. DEFERRED — §20.7.

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
| Inactive → Active | A new membership starts | The returning member — same record (§4.9) |
| Active / Inactive → Banned | Staff action, manager permission | Membership and billing unaffected (§13.6) |
| Banned → previous | Ban lifted or expires | Returns to whatever their memberships imply |
| Inactive → Archived | Staff action, or automatic after a long period | Retention-driven |
| Archived → Inactive/Active | Return (§4.9) | Un-archived, archival kept in history |
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
| Active → Frozen | Freeze begins | Within the Type's limits (§6.7) |
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
| `members.identity_permanent_across_memberships` | The record outlives every membership. Nothing commercial causes a second record for the same person at the same gym. |
| `members.member_number_assigned_on_creation` | Automatically. Never typed by staff. |
| `members.member_number_immutable` | No permission edits it. |
| `members.member_number_unique_per_gym` | Scoped to the gym, matching the tenancy model. |
| `members.member_number_never_reused` | Not after archival, erasure, or merge. |
| `members.member_number_searchable` | Findable as directly as a surname. |
| `members.merged_number_retired_and_redirects` | The survivor keeps its own; the absorbed number resolves to the survivor forever. |
| `members.returning_member_reuses_record` | A returning person is a new membership, never a new member. |
| `members.duplicate_detection_warns_never_blocks` | |
| `members.merge_requires_permission_and_reason` | And is audited, previewed, and leaves a redirect. |
| `members.archive_does_not_delete` | |
| `members.erasure_preserves_financial_and_audit_integrity` | Erasure removes identifying data; financial records and the audit trail survive in non-identifying form. |
| `members.no_detailed_medical_data_stored` | Emergency contacts, optional internal notes, and an optional medical attention flag. No conditions, medications, diagnoses, or declarations. |
| `members.medical_attention_flag_carries_no_detail` | A flag only. |
| `members.medical_flag_excluded_from_exports_reports_and_logs` | Visible to staff on duty at the point of use, nowhere else. |
| `members.medical_flag_removable_on_request` | |
| `members.age_policies_configurable` | Every age threshold is a per-gym setting, never hard-coded. |
| `members.staff_and_member_are_linked_not_merged` | |

### 16.2 Membership

| Code | Rule |
|---|---|
| `memberships.one_active_primary` | At most one primary membership in force at any instant. |
| `memberships.primaries_never_overlap` | Including a queued future one — adjacent or with a gap, never overlapping. |
| `memberships.add_ons_may_overlap` | Freely, with each other and with a primary. |
| `memberships.add_on_requires_primary` | Default; a Membership Type may permit standalone (a PT-only client who never uses the gym floor). |
| `memberships.end_after_start` | |
| `memberships.at_most_one_queued_primary` | Default. |
| `memberships.backdating_requires_permission_and_reason` | And never retroactively creates or removes visits. |
| `memberships.price_snapshot_at_agreement` | A Type price change never re-prices an existing Instance. |
| `memberships.type_and_instance_are_distinct` | Never collapsed into one concept. |
| `memberships.editing_a_type_never_alters_an_instance` | Future sales and renewals only. |
| `memberships.retiring_a_type_preserves_instances` | A retired Type stops being sellable; history stays intact. |
| `memberships.retired_type_may_still_renew_existing_instances` | Grandfathering is a deliberate retention tool. |
| `memberships.upgrade_prorated_automatically` | Unused value credited against the new price. |
| `memberships.upgrade_calculation_shown_before_confirmation` | Itemized. |
| `memberships.proration_credit_carries_forward_not_refunded` | Refunds remain a deliberate staff action. |
| `memberships.cancellation_rules_configurable_per_gym` | Notice, cooling-off, refunds, fees. |
| `memberships.cancellation_configuration_cannot_undercut_statutory_minimums` | Settings have a legal floor. |
| `memberships.freeze_within_period` | A freeze cannot extend beyond the period it freezes. |
| `memberships.freeze_within_type_limits` | Count per year, days per year, minimum duration — declared by the Membership Type. |
| `memberships.freeze_extends_end_date` | By the days actually frozen. |
| `memberships.freeze_early_end_recalculates_extension` | 9 days frozen extends by 9, not by the 30 requested. |
| `memberships.freeze_hold_fee_configurable` | Per Membership Type; default €0. |
| `memberships.freeze_preserves_entitlement` | Not consumed, not forfeited. |
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
| `memberships.location_scope_declared` | Every Membership Type states which locations it admits. |

### 16.3 Attendance

| Code | Rule |
|---|---|
| `attendance.every_attempt_recorded` | Including denials, with a reason. |
| `attendance.duplicate_scan_within_window_is_one_check_in` | Default 90 seconds, same credential, same device. |
| `attendance.one_visit_per_local_calendar_day_per_location` | Re-entry the same day at the same location joins the existing visit. A different location the same day is a separate visit. |
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
| `access.balance_warns_by_default` | Strict blocking optional, configured by amount and days overdue with no shipped default. |
| `access.grace_periods_configurable` | Membership access grace defaults to 3 days; payment grace to 7. |
| `access.age_policy_evaluated_from_configuration` | Never from hard-coded ages. |
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
| `gymos.members.member.registered` | A member record is created; carries the assigned Member Number |
| `gymos.members.member_number.assigned` | Number issued — separate, because card printing and welcome messaging key off it |
| `gymos.members.member_number.retired` | A number was retired by a merge or erasure |
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
| `gymos.memberships.membership_type.created` | A new product is sellable |
| `gymos.memberships.membership_type.updated` | Price or entitlement changed — affects future sales only |
| `gymos.memberships.membership_type.retired` | No longer sellable; existing Instances unaffected |
| `gymos.memberships.membership.agreed` | An Instance was sold — including future-dated, before it starts |
| `gymos.memberships.membership.started` | Became active |
| `gymos.memberships.membership.renewed` | New period on the same membership |
| `gymos.memberships.membership.type_changed` | Upgrade or downgrade; carries direction and any proration |
| `gymos.memberships.membership.frozen` | Carries the freeze window and any hold fee |
| `gymos.memberships.membership.end_date_extended` | A freeze extended the Instance; carries the days added |
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
12. **Do not collapse Membership Type and Membership Instance** into one concept, however tempting the
    early simplicity looks. Editing a price would then rewrite four hundred agreements.
13. **Do not derive, reformat, or regenerate a Member Number.** It is assigned once and read forever.
14. **Do not hard-code an age.** Every threshold is configuration.
15. **Do not store medical detail** anywhere, in any field, including internal notes — and say so in the
    interface where notes are written.
16. **Do not let cancellation configuration fall below a statutory minimum.**
17. **Do not implement anything marked DEFERRED in §20.**

---

## 20. Decision Register

The record of what was decided, by whom, and what remains open. **Frozen means this register is the
only way the specification changes:** a new decision is added here with its date and reasoning, and the
body of the document is updated to match. Nothing is quietly reinterpreted.

### 20.0 Approved product decisions — 2026-08-05

Eleven decisions were approved by the product owner. All previously blocking questions are resolved and
M2 implementation may begin.

| # | Decision | Recorded in |
|---|---|---|
| **D1** | **Freezes extend the membership**, with a configurable hold fee defaulting to €0. | §6.7 |
| **D2** | **One visit equals one local calendar day per location.** | §11.4 |
| **D3** | **Grace periods are configurable** — defaults 3-day membership access, 7-day payment. **Warnings by default; strict blocking optional.** | §9, §13.7 |
| **D4** | **Upgrades are automatically prorated**, unused value credited, and the **calculation is shown before confirmation**. | §10.2 |
| **D5** | **No detailed medical data in the MVP** — emergency contacts, optional internal notes, and an optional medical attention flag only. | §4.13 |
| **D6** | **Minors are supported from day one**, with guardian details and **configurable age policies**. | §4.10 |
| **D7** | **Multi-location is modelled from day one; a single location ships enabled by default.** | §14 |
| **D8** | **Cancellation rules are configurable per gym** — notice, cooling-off, refunds, fees. | §10.1 |
| **D9** | **Members have a permanent identity that persists across memberships.** | §4.3, §4.9 |
| **D10** | **Every member receives a permanent GymOS Member Number.** | §4.3 |
| **D11** | **Membership Types and Membership Instances are separate concepts.** | §3, §6.1 |

**Notes on how three of these were interpreted**, so implementation does not have to guess:

- **D1** — "extends" means by the days *actually* frozen, so ending a freeze early extends by less. The
  hold fee is per Membership Type, and €0 is a real configured value rather than a disabled feature.
- **D4** — "remaining value credited" is implemented as a credit against the new price, not a refund. A
  credit exceeding what is owed carries forward; refunding it stays a deliberate staff action, because an
  automatic refund path is how money leaves a business unnoticed.
- **D8** — configuration has a **legal floor**. A gym may be more generous than the statutory minimum,
  never less, and implementation must treat statutory values as a lower bound on the setting rather than
  as another default. The statutory values themselves are §20.3.

### Deferred — awaiting legal review

Not guessed, and not implemented. Each has a **mechanism** to build now and **values** that must come
from counsel. Building the mechanism first is deliberate: it makes the legal answer a configuration
change rather than a release.

---

#### 20.1 Age policy values and guardian consent evidence

**Build now:** configurable age thresholds (§4.10) and the guardian relationship with a recorded consent
event.

**Awaiting counsel:** the correct values for Cyprus — age of majority for contracting, minimum ages for
unsupervised and unaccompanied access — and what constitutes acceptable **evidence** of guardian consent
(a signature, a recorded acceptance, an identity check, a retained copy).

**Why it cannot be guessed.** Safeguarding and insurance both depend on it, and a gym that admitted a
14-year-old unaccompanied because a default said 16 has a liability problem our default created.

**Constraint until resolved:** ship the settings with no default values populated for a live gym. A gym
must choose its ages during onboarding, and onboarding must say why.

---

#### 20.2 Consent wording and retention for the medical attention flag and internal notes

**Build now:** the flag and the notes, with the handling constraints in §4.13.

**Awaiting counsel:** the consent wording shown to the member, the retention period for both, and
whether a structured health declaration (PAR-Q) may be added later and on what basis.

**Why it cannot be guessed.** A flag stating that a member may need medical attention permits an
inference about their health, which is why §4.13 treats it with care disproportionate to its size. The
wording is what makes holding it lawful.

**Constraint until resolved:** a structured health declaration **must not be built**.

---

#### 20.3 Statutory cancellation and cooling-off values

**Build now:** the configurable cancellation rules (§10.1) and the floor mechanism.

**Awaiting counsel:** the statutory minimums under Cyprus and EU consumer law — particularly
cooling-off rights for memberships sold online or off-premises, and whether they differ for a committed
term.

**Why it cannot be guessed.** A configured notice period that undercuts a statutory right is
unenforceable, and the gym discovers this in a dispute.

**Constraint until resolved:** ship the settings; do not populate defaults for a live gym.

---

#### 20.4 Attendance retention period

**Build now:** retention as an enforced, configurable rule (§12) with irreversible aggregation.

**Awaiting counsel:** how long identifiable visit records may be kept, what survives as aggregate, and
how this interacts with financial-record retention obligations. Proposed starting point: **24 months**
identifiable, then aggregation.

**Why it cannot be guessed.** Attendance is a record of a person's movements; keeping it indefinitely is
not defensible under data minimisation, and deleting it too early may conflict with a retention
obligation elsewhere.

**Blocks go-live, not the build.**

---

### Deferred — scheduled after M2

Product scope decisions, not legal ones. Each has a recommended shape so that the M2 model does not
have to be reworked to accommodate it later.

---

#### 20.5 Family and corporate memberships

**Deferred.** Both change who pays, who is entitled, and what happens when the group changes — a child
turning 18, an employee leaving.

**Kept in the M2 model so adding them stays additive:** the **payer** relationship is already separate
from family and corporate (§5), because real arrangements do not follow the household. Deriving the payer
from a family group produces wrong invoices, and that mistake is expensive to unpick after billing has
run.

**Open when resumed:** is the family a pricing construct or an entitlement pool; what happens when a
member leaves the group mid-period; does the corporate agreement verify employment, and how.

---

#### 20.6 Minimum commitment terms

**Deferred.** A 12-month committed agreement interacts with cancellation, freezing, and upgrades, and
usually with price.

**Kept in the M2 model:** nothing assumes every Instance is rolling. That assumption is the expensive
one to reverse, so it is not made.

**Open when resumed:** term lengths, early-termination fees (§20.3 legal floor applies), and whether a
committed term may be frozen at all.

---

#### 20.7 Franchise sharing

**Deferred, and explicitly out of the MVP.** Cross-tenant access is a commercial and data-protection
arrangement before it is a feature: revenue attribution per visit, who is data controller, and what one
franchisee may see of another's members.

**Mechanism already exists** for later: versioned contracts between services, per the frozen
architecture — never a shared database.

**Resume only** with a named franchise customer and a signed agreement describing the commercial terms.

---

#### 20.8 Guest allowances attached to a Membership Type

**Deferred.** A **day pass** is supported in M2 — a member plus a single-visit purchase, which the model
already allows and reception needs from day one. What is deferred is a Type declaring "3 guest visits per
month", which is additional entitlement machinery.

---

#### 20.9 Merge policy details

**Deferred, needed before go-live.** The shape is decided in §4.11 and §4.3: manager permission,
mandatory preview, forward-only with a permanent redirect, absorbed Member Number retired forever, and
refusal when both records hold an active primary Instance.

**Open:** whether a merge is ever reversible after a payment has been taken against the survivor.
Recommendation stands at **no** — reversal is where the corruption risk lives.

---

#### 20.10 Trials

**Deferred.** Implement as a short primary Instance on a Trial Membership Type, with a rule that a person
may hold at most one trial ever, enforced by the duplicate-detection signals (§4.11).

**Open:** length; one per person ever or per period; whether payment details are required; what happens
when a trial lapses without converting.

---

#### 20.11 Member Number presentation format

**Deferred, cosmetic.** The properties in §4.3 are frozen: permanent, never reused, unique per gym,
human-readable, immutable, automatically assigned.

**Open:** whether the number carries a location or year component, its length, and whether it is padded.
None of this blocks implementation, and all of it is easier to settle once a gym has seen a card.

**Constraint:** whatever the format, nothing may **derive meaning** from it in code. A number that
encodes a location becomes wrong the day a member transfers.

---

## 21. Implementation readiness

**Frozen. M2 implementation may begin.**

| | Status |
|---|---|
| Blocking product decisions | **All resolved** — §20.0, eleven approved decisions |
| Legal review items | **4 deferred** (§20.1–§20.4), each with a mechanism to build now and values to populate from counsel |
| Post-M2 features | **7 deferred** (§20.5–§20.11), each with the M2 model shaped so they stay additive |
| Guardrails | §19 — seventeen things an implementation must not quietly undo |

**What "frozen" obliges.** Implementation follows this document. A change requires a decision recorded
in §20 first, with its reasoning, and the body updated to match. If implementation discovers that a
Decided statement is impossible or wrong, that is a finding to bring back here — not a judgement call to
make in code.

**Three things to confirm before a live gym uses this**, none of which blocks building:

1. Legal values for §20.1–§20.4 populated from counsel's advice.
2. Merge policy details settled (§20.9).
3. The accepted limitation in §11.10 — an offline gateway may admit someone whose membership changed
   moments earlier — communicated to the gym owner in writing, rather than discovered in a support
   ticket.
