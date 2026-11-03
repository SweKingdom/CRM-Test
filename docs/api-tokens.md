# Personal API tokens (US-142)

A personal API token lets a script or an AI assistant act as you, with your roles and permissions.

## Use

| Action | Request |
|---|---|
| Create (browser session only) | `POST /api/v1/me/tokens` `{"name": "My script", "expiresInDays": 90}` → 201 with `token`, shown once |
| List your tokens | `GET /api/v1/me/tokens` → name, prefix, expiry, last used, revoked |
| Revoke | `DELETE /api/v1/me/tokens/{id}` → 204 |
| Call the API | `Authorization: Bearer zph_...` |

- `expiresInDays`: whole number 1–365, default 90. A token past its expiry, a revoked token, or a token of a deactivated or deleted user is refused with 401.
- At most 10 active tokens (not revoked, not expired) per user; the 11th is refused with 422 `TOKEN_LIMIT`.
- A token cannot create tokens (403). Create them while signed in.
- Copy the token when it is created: Zephyr keeps only a hash and cannot show it again. Lose it, revoke it and create a new one.

## Storage

The token is `zph_` + 43 base64url characters (32 random bytes). The database stores the lowercase hex SHA-256 in `api_token.token_hash` and the algorithm name in `api_token.hash_algorithm` (`sha256`). Unsalted SHA-256 is accepted by the product owner (question #74): the secret has 256 bits of entropy, and a deterministic hash is needed to find the token on each request. To change the algorithm later, change `ApiTokens.Hash` and the algorithm name; old rows keep working until they expire or are re-issued. Hashes are compared in constant time.

## Secret scanning (manual step, repository admin)

GitHub custom secret-scanning patterns are set in the repository settings, not in a file. Add one so a committed token is flagged:

1. Repository **Settings → Advanced Security → Secret scanning → Custom patterns → New pattern**.
2. Name: `Zephyr personal API token`.
3. Secret format: `zph_[A-Za-z0-9_-]{43}`.
4. Test it against a token from a local run, then publish and enable push protection.
