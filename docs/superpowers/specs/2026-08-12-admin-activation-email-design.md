# Administrator activation & password-reset email — Design

**Status:** drafted 2026-08-12, **awaiting approval**, not yet built.
**Prerequisite:** **ADR-53** (proposed, in `GymOS-Specification/ADR-CHANGELOG.md`) — a minimal
platform email sender ahead of `GymOS.Modules.Notifications`. The architecture is frozen; this
increment does not start until that ADR is approved.

## 1. What this fixes

`POST /users` has shipped administrators nobody can use since M1.3.

The invite creates the account in `PendingActivation` with its roles and no password, and that is
correct — ARCHITECTURE.md §3.6: *an administrator never sets another administrator's password.* The
owner is meant to set it through a link they receive. **That link has never existed**, so the only
way to give any account a password today is the `Seed:SuperAdmin` configuration path.

`InviteUserCommand`'s own doc comment has said so since M1: *"cannot authenticate until its owner
sets a password through the activation flow, which lands with email delivery."* This is that.

## 2. Scope

### In

| Capability | Surface |
| --- | --- |
| Invitation email on `POST /users` | The existing endpoint gains a side effect |
| Re-send an invitation | `POST /api/v1/users/{id}/resend-invitation` |
| Administrator-initiated password reset | `POST /api/v1/users/{id}/reset-password` (§3.6, never implemented) |
| Set a password from a link | `POST /api/v1/auth/set-password` — **anonymous**, token-bearing |
| Check a link before showing a form | `GET /api/v1/auth/set-password?token=…` — anonymous, validity only |
| The console page the link lands on | `GymOS-Platform`: `/set-password` |

### Out, and why

- **Self-service "forgot password"** — an anonymous endpoint that takes an *email*. It needs its own
  anti-enumeration treatment (identical answer whether or not the address exists, and a response time
  that does not betray the difference), and it is a different decision about who may initiate account
  recovery. Natural follow-on; deliberately not folded in.
- **Everything §4.1 owns** — templates table, preferences, digests, in-app channel, SMS/WhatsApp/push.
  Boundaries are in ADR-53, which is explicit that a third message means revisiting the ADR.
- **Member-facing email of any kind.** Waits for the Notifications module.
- **Email change / verification flows.** Not asked for, and they need their own token semantics.

## 3. The decisions that shape the build

### D1 — The token is a credential, and gets `RefreshToken`'s treatment exactly

32 random bytes from `RandomNumberGenerator`, base64url-encoded for the URL, **SHA-256 at rest**. The
database never holds a usable token, so a leaked backup does not hand over every pending account.

This is not a new pattern to invent: M1.2 already settled it for refresh tokens, down to the
generator and the hashing. The same shape, in the same repository, for the same reason.

**Single-use.** Consumed on success, and consumed *before* the password is validated against the
policy — otherwise a weak-password attempt would burn nothing and the link would be replayable for
as long as it lived.

### D2 — Two lifetimes, because the two messages carry different risk

| Message | Lifetime | Why |
| --- | --- | --- |
| Invitation | **72 hours** | It is sent to somebody who is not yet expecting it and may be away; too short and every invite becomes a support request. The account has no password to protect yet. |
| Password reset | **1 hour** | The account already exists and already has a credential. A live reset link is an alternative way in, so it should not outlive the reason it was created. |

Both are configuration with these as defaults, not constants buried in a handler.

### D3 — One live token per user

Issuing a new one invalidates the previous one. Otherwise a re-send leaves the first link live, and
"I re-sent it because the first might have been intercepted" stops being true.

### D4 — The anonymous endpoints answer identically for every failure

`set-password` is reachable without authentication, so it is a guessing surface. Expired, already
used, never existed, and belongs-to-a-deleted-user all return **the same** `410 Gone` with the same
body. A distinct "expired" message would confirm that a token *had* existed — and the operator-facing
distinction it buys is worth nothing next to that.

The sign-in rate limiter (`Security:RateLimiting`, per IP) covers both anonymous endpoints. A token
is 256 bits; the limiter is there for the noise, not because guessing is plausible.

`GET` exists so the console can show "this link has expired, ask for another" instead of a form that
fails on submit. It returns validity and nothing else — no email, no name, no roles.

### D5 — Setting a password ends every session and activates the account

One transaction: set the hash, rotate the security stamp, revoke every refresh token, and move
`PendingActivation → Active` through `UserStatusTransitions` (never by assigning the enum).

Stamp rotation and revocation are M1.2/M1.3's rule for change-password — *"a thief must not be able
to refresh back in"* — and a reset is the same event with more reason to apply it: the person may be
resetting *because* they think someone else is in.

A reset on an already-`Active` account leaves the status alone. Only `PendingActivation` activates,
and a `Suspended` account cannot set a password at all: it would be a suspended user quietly
regaining access.

### D6 — The email is sent after the commit, and its failure does not undo the invite

The outbox exists but **its processor does not** (capture side only, by deliberate design — it lands
with the first event-handler need). So the send is inline, after `SaveChangesAsync` returns.

The consequences, stated rather than hidden:

- A crash between commit and send loses that one email. **`resend-invitation` is the recovery path**,
  which is why it is in scope rather than a nicety.
- An SMTP failure is logged and surfaced to the caller as a partial success — the account **is**
  created; the response says the mail did not go. Rolling the invite back would be worse: the
  administrator did the thing they asked for, and a silent rollback teaches them to distrust the
  screen.
- When the outbox processor lands, this moves onto it and both of the above go away.

### D7 — The link points at the console, and the console is configuration

`Console:BaseUrl` (e.g. `https://admin.gymos.example`) plus the path. The backend does not guess its
own frontend's address, and it **fails fast at startup when unset in a non-development environment**
— the same treatment `Security:Jwt:SigningKey` gets, for the same reason: an email that goes out with
a `localhost` link is not recoverable after the fact.

### D8 — The token is never logged, and never leaves in a response body

Not at Debug, not in the audit trail, not in the `set-password` echo. The audit entries record *that*
an invitation was sent and to whom (`user.invitation_sent`, `user.password_reset_requested`,
`user.password_set`) — never the token itself. A trail that records credentials is a second copy of
them.

### D9 — Email is off by default and fails loudly when it is on

`Email:Enabled` defaults to **false**: the integration tests, and a developer who has not started the
Mailpit profile, must not depend on an SMTP server existing. Disabled, the sender logs the recipient
and subject at Information and returns success.

When enabled, missing configuration is a **startup** failure, not a first-send failure — the same
fail-fast rule as the JWT key. `Email:Smtp:{Host,Port,UseStartTls,Username,Password}` and
`Email:From:{Address,DisplayName}`; development points at Mailpit (`localhost:1025`), which is
already in `docker-compose` under the `tools` profile.

### D10 — Two messages, rendered in code, in one place

A tiny `EmailTemplates` class with two methods returning subject + plain-text body + HTML body. Not a
template engine, not a table (ADR-53's boundary), and not string-concatenated at the call site.

Plain text alongside HTML, because a security email that renders as an unstyled blob in a strict
client is a security email nobody trusts. Both bodies HTML-encode the display name — it comes from
whoever typed the invite.

## 4. Persistence

One migration, `AddUserActivationTokens`:

```
platform.user_activation_tokens
  id                uuid  pk
  user_id           uuid  fk -> platform.users (cascade)
  token_hash        bytea not null            -- SHA-256; unique index
  purpose           int   not null            -- Invitation | PasswordReset
  expires_at_utc    timestamptz not null
  consumed_at_utc   timestamptz null
  created_at_utc    timestamptz not null
  created_by_user_id uuid null  fk -> users (set null)   -- provenance, the PlanPriceId precedent
```

- **Unique index on `token_hash`** — the lookup, and the guarantee that two tokens cannot collide.
- **Index on `(user_id, purpose)`** for D3's invalidate-the-previous.
- **Not audited as an entity.** The rows carry a credential hash and nothing a human reads; the
  *events* are audited instead (D8).
- **`RetentionJob` purges consumed and long-expired rows**, alongside the refresh-token purge M4.1's
  audit added. A token table that only grows is the same defect that job already exists to prevent.

## 5. Endpoints

| Method | Route | Auth | Notes |
| --- | --- | --- | --- |
| POST | `/api/v1/users` | `users.manage` | Unchanged contract; now also sends the invitation |
| POST | `/api/v1/users/{id}/resend-invitation` | `users.manage` | Only for `PendingActivation`; 409 otherwise |
| POST | `/api/v1/users/{id}/reset-password` | `users.manage` | §3.6's endpoint, finally built. Refused for `PendingActivation` (that is a re-send) and for `Suspended` |
| GET | `/api/v1/auth/set-password` | **anonymous** | `?token=` → `{ valid: true }` or 410 |
| POST | `/api/v1/auth/set-password` | **anonymous** | `{ token, newPassword }` → 204 |

The OpenAPI snapshot moves: three operations added, one unchanged. Regenerated deliberately and read
semantically, per R13.

## 6. Console (GymOS-Platform)

Small, and required — without it the link 404s:

- **`/set-password`** (outside the authenticated shell, like `/login`): reads `?token=`, calls `GET`
  to check it, shows either the set-password form or "this link has expired, ask an administrator for
  another". On success, redirects to `/login` with a note to sign in.
- **Users list/detail**: a **Re-send invitation** action for `PendingActivation` rows and a **Send
  password reset** action otherwise, both behind `users.manage`, both confirming first.

## 7. Verification

**Unit.** Token generation is unpredictable and hashed, never stored raw; expiry and single-use are
enforced on the domain object rather than by the handler; `PendingActivation → Active` goes through
the transition table and `Suspended` is refused.

**Integration, over real PostgreSQL.**

- The whole journey: invite → read the token from the outbox of a fake sender → set a password →
  sign in with it. The proof that the hole in §1 is closed.
- A token works **once**: the second attempt is 410, and the password is unchanged.
- Expiry is refused (clock moved past it, not a `Thread.Sleep`).
- **All four failure modes return byte-identical responses** — the anti-oracle assertion, and the one
  most likely to rot as somebody adds a helpful message.
- Re-sending invalidates the previous token (D3), asserted by using the *first* one afterwards.
- Setting a password revokes existing refresh tokens and rotates the stamp: a refresh token captured
  before the reset is dead after it.
- A suspended user cannot set a password.
- With `Email:Enabled=false` nothing is sent and nothing fails.

**Live.** Invite an administrator against the compose stack with the Mailpit profile running, open
the message in Mailpit's UI, follow the link in a browser, set a password, sign in with it.

## 8. Deferred, with the dependency each waits on

| Deferred | Waits on |
| --- | --- |
| Self-service forgot-password | Its own increment (§2) |
| Moving the send onto the outbox | The outbox processor |
| Templates, preferences, digests, other channels | `GymOS.Modules.Notifications` (§4.1) |
| Member-facing email | As above |
| Email change + verification | Its own token semantics |

## 9. Risks

- **ADR-53 is the real gate.** If the deviation is not accepted, the honest alternatives are building
  §4.1 first or shipping the activation flow with the link returned in the API response for an
  administrator to pass on by hand. Both are worse; neither is unreasonable.
- **An email that cannot be delivered is invisible to the sender.** Bounces, spam filing, and a wrong
  address all look identical from here — the API reports "sent to the SMTP server", never "arrived".
  The re-send action is the only mitigation this increment has, and that limit belongs in the console
  copy rather than in a document.
- **`Console:BaseUrl` is a foot-gun**: wrong once, and every link in every invitation is wrong and
  already delivered. Hence D7's fail-fast, and hence it being the first thing to check in the live run.
