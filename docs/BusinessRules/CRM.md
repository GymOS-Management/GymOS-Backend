<!--
  DRAFT — not an approved specification.

  This document collects what is known and what is undecided about a module that has not been
  built. It is a starting point for the scoping conversation, not a commitment: the Golden Rule
  (GymOS-Specification/PRODUCT-PRINCIPLES.md) is the scope authority, and every rule below must be
  confirmed with the product owner before it is implemented. Rules marked (?) are assumptions.
-->

# CRM — Business Rules

**Module:** `GymOS.Modules.Crm` · schema `crm` · tenant-scoped
**Status:** not started.

## Purpose

Turning prospects into members and keeping members from leaving. Leads, follow-ups, campaigns, and
retention signals.

This is the module where **consent and communication law**, not domain modelling, is the hard part.
Getting the rules wrong here is a regulatory exposure, not a bug.

## Entities

| Entity | Notes |
|---|---|
| `Lead` | A prospect, with source and status. |
| `LeadActivity` | Calls, visits, trials, notes — the follow-up history. |
| `Campaign` | An outbound effort with an audience and a channel. |
| `CommunicationConsent` | Per member/lead, per channel, per purpose, with when and how it was given. |
| `RetentionSignal` | A derived risk indicator (attendance dropping, membership lapsing). |
| `Task` | A follow-up assigned to a staff member. |

## Business Rules

| Code | Rule |
|---|---|
| `crm.consent_required_before_marketing` | No marketing message without a recorded consent for that channel and purpose. **GDPR Art. 6/7 and the ePrivacy rules.** Transactional messages (a booking confirmation) are a different basis and not covered by this. |
| `crm.consent_withdrawal_immediate` | Withdrawal takes effect immediately and is recorded with its timestamp. A queued campaign must re-check consent at send time, not at scheduling time. |
| `crm.consent_per_channel_and_purpose` | Consent to email about bookings is not consent to SMS about offers. One flag for "marketing" is the mistake this rule exists to prevent. |
| `crm.lead_converts_to_member_once` | Conversion creates one member and links the lead; a second conversion is refused. |
| `crm.lead_status_transitions_validated` | `New → Contacted → Trial → Converted / Lost`, as a state machine. |
| `crm.campaign_audience_snapshot` | An audience is snapshotted at send, so "who received this" is answerable later even as the underlying segment changes. |
| `crm.no_pii_in_campaign_content_logs` | Message bodies containing personal data are not written to application logs. |
| `crm.retention_signal_is_advisory` | A signal raises a task; it never takes an automatic action against a member's account. |
| `crm.unsubscribe_link_required` | Every marketing message carries one. |

## Invariants

- Consent is append-only: a withdrawal is a new record, so the history of what was permitted when is
  reconstructable. Overwriting consent destroys the only evidence that sending was lawful.
- A lead is either open, converted (with a member reference), or lost with a reason.
- A campaign send records the consent state relied on at the moment of sending.
- Retention signals are derived and recomputable; none is authoritative state.

## Future Rules

- Lead scoring and automated nurture sequences (the automation engine, AD-30, already exists).
- Referral tracking and rewards.
- Win-back campaigns for lapsed members.
- Attribution: which campaign produced which membership.
- Preference centre for members to manage their own consents.

## Dependencies

- **Members** (conversion target, retention subject), **Memberships** (lapse signals), **Attendance**
  (engagement signals), platform **notifications**, **automation**, **integrations** (email/SMS
  providers), **jobs**.
- **Events:** `gymos.crm.lead.captured`, `.converted`, `.lost`, `gymos.crm.consent.granted`,
  `.withdrawn`, `gymos.crm.campaign.sent`.

## Open Questions

1. **Consent model** — the single most important question here. Which purposes and channels, what
   wording, and what evidence is retained? Gets the module either compliant or not.
   **Verify with legal counsel before implementation.**
2. Cyprus and EU rules on **unsolicited commercial communication** (ePrivacy, and the Cyprus
   implementation): is soft opt-in for existing customers available, and under what conditions?
3. Are leads personal data subject to the same erasure rights as members? (Almost certainly yes.)
   Retention period for an unconverted lead?
4. Does the platform send messages itself, or hand off to the gym's own provider account? Changes who
   is controller and who is processor — a contractual question as much as a technical one.
5. Should retention signals ever act automatically (pause billing, send an offer), or always raise a task?

---

## How this document is used

Before the module is implemented: walk the Business Rules against the Golden Rule and cut what does
not reduce work for the gym owner. Then turn the survivors into `IBusinessRule` implementations
(domain, pure) or Application-layer guards (cross-aggregate), with the codes below as their `Code`
values. Answer the Open Questions first — every one of them changes the shape of the entities.
