# Account Activation — design, pending Email Delivery

**Status:** design only. Nothing in this document is implemented, by decision.
**Related:** AD-4 (auth), AD-22 (permissions), ARCHITECTURE.md §2.1 (users), §3.1 (`/auth`), §3.6 (`/users`)
**Blocked on:** `IEmailSender` and the notification/job infrastructure (ARCHITECTURE.md §4.1, AD-23)

---

## Where the flow stops today

`POST /api/v1/users` creates an administrator in **PendingActivation** with `password_hash`
null. The account holds its roles, appears in the console, and resolves permissions — and cannot
authenticate. Sign-in treats a null hash exactly like an unknown email, so an invited account is
not even detectable from the login endpoint.

That is the end of the implemented path. There is deliberately **no** way for an administrator to
give another account a password.

## What was explicitly not built, and why

**No temporary passwords.** A password a second person has seen is not a credential: it has to be
transmitted somehow, it lands in a chat log or a ticket, and the audit trail can no longer
distinguish the owner's actions from the issuer's. It also has no natural expiry — "temporary"
is a promise about intent, not a property of the value.

**No admin-set passwords.** Same defect, plus it makes account takeover a feature: whoever holds
`users.manage` could then act as any administrator, and nothing in the trail would show it.
ARCHITECTURE.md §3.6 already anticipates this — the approved endpoint is
`POST /users/{id}/reset-password`, which *sends a link*, never sets a value.

**No activation without email**, even guarded by a flag. A half-built activation path with a
development shortcut is exactly the shortcut that survives into production.

The consequence is stated plainly rather than worked around: **until Email Delivery lands, the
only account that can sign in is the one created by the `Seed:SuperAdmin` path.** That is a
temporary operational limitation, not a design gap.

## Extension points that already exist

The domain is ready for the final step; only the delivery mechanism is missing.

| Extension point | State | Role in activation |
|---|---|---|
| `User.SetPasswordHash(hash, now)` | **exists** | The activation transition itself: `PendingActivation → Active`, rotates the security stamp, clears any lockout. Refuses a suspended account, so activation cannot be used to escape suspension. Covered by `UserStatusTransitionTests`. |
| `UserStatusTransitions` | **exists** | `PendingActivation → Active` is already a declared legal transition; no state-machine change is needed. |
| `PasswordPolicy.Validate` | **exists** | The one place a password is judged acceptable. The activation handler calls it — the rule cannot drift between activation, reset, and change-password. |
| `IPasswordHasher` | **exists** | Argon2id with parameters from configuration. |
| `IAuditTrail.Record` | **exists** | `auth.activated` / `auth.password_reset` are non-entity actions, exactly what this seam is for. |
| Rate limiter policy | **exists** | `RateLimitingExtensions` already has the shape; activation and reset endpoints need their own policy instance. |
| `IEmailSender` | **missing** | Delivery. Approved in ARCHITECTURE.md §4.1; every send is a job, so retry and dead-lettering come from the job system rather than from this flow. |
| Activation token storage | **missing** | New table, see below. |

## The flow, once Email Delivery exists

### 1. Token issuance

A new aggregate, `platform.account_activation_tokens`, deliberately shaped like
`refresh_tokens` — the same threat, so the same treatment:

| Column | Notes |
|---|---|
| `user_id` | FK users |
| `token_hash` | SHA-256 of an opaque 32-byte token. **The raw token exists only in the emailed link**; a database read yields nothing usable. |
| `purpose` | `Activation` / `PasswordReset`. One table, because the mechanics are identical and two would drift. |
| `expires_at` | Short — hours, not days. A link that outlives the reason it was sent is a standing credential. |
| `consumed_at`, `consumed_by_ip` | Single use, enforced by the domain. |
| `invalidated_at`, `invalidation_reason` | Issuing a new token invalidates outstanding ones, so a resend cannot leave two live links. |

Reuse of a consumed token is treated the way refresh-token reuse already is: refused, audited,
and every outstanding token for that user invalidated. The leak is the signal.

### 2. Endpoints

| Method | Route | Auth | Notes |
|---|---|---|---|
| POST | `/api/v1/auth/activate` | anonymous | Body: token + new password. Validates, sets the hash (activating the account), consumes the token. |
| POST | `/api/v1/auth/forgot-password` | anonymous | Rate-limited per IP. **Always returns 204**, whether or not the address exists — otherwise it becomes an account-existence oracle, which is the same reason `login` is deliberately vague. |
| POST | `/api/v1/auth/reset-password` | anonymous | Same as activate, for an already-active account. |
| POST | `/api/v1/users/{id}/reset-password` | `users.manage` | Administrator triggers a reset link. Sets no password and returns none — matches §3.6. |
| POST | `/api/v1/users/{id}/resend-invitation` | `users.manage` | Re-issues, invalidating the previous token. |

Each is anonymous by necessity and must be added to the `DeliberatelyAnonymous` list in
`EndpointConventionTests`, with its reason — the R11 gate will otherwise fail, which is the gate
working.

### 3. Rules the implementation must keep

1. **Uniform responses.** Unknown, expired, consumed, and invalidated tokens all answer the same
   way. Distinguishing them tells an attacker which guesses are close.
2. **Activation is a password write**, so `PasswordPolicy` and `IPasswordHasher` apply unchanged.
3. **Consuming a token rotates the security stamp** (already a side effect of
   `SetPasswordHash`) and must revoke every refresh token for that user — a reset exists because
   the credential may be compromised, so existing sessions cannot survive it.
4. **A suspended account cannot be activated or reset.** The domain enforces this today; the
   handler should return the same opaque failure rather than explaining why.
5. **Every outcome is audited** through `IAuditTrail`, including failures, with the reason in the
   entry and *not* in the response.
6. **Email content carries no secret but the link**, and the link is single-use and short-lived.

### 4. Retention

Consumed and expired tokens are purged by the same retention job that prunes audit partitions and
expired refresh tokens. Nothing here needs its own scheduler.

## Migration impact

Additive: one new table, one new set of endpoints. No existing column, endpoint, or response shape
changes, so it does not affect the v1 contract beyond adding paths — additive within the version
under AD-37 §6.1.
