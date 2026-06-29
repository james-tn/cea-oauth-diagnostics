# Delivering files from a bot/agent: Teams vs. M365 Copilot

A small, self-contained reference showing how a Microsoft 365 Agents (Bot Framework) bot can
deliver a file to a user, and **what works on each surface** — native Teams vs. the embedded
M365 Copilot experience.

## TL;DR — support matrix

| Mechanism | Native Teams | Embedded M365 Copilot |
|---|---|---|
| **Download link** (markdown link / Adaptive Card `Action.OpenUrl` to a signed/SAS URL) | ✅ | ✅ |
| **Teams `FileConsentCard`** (upload to the user's OneDrive) | ✅ *only with `supportsFiles: true`* | ❌ not supported |

**Recommendation:** use a **download link** for cross-surface file delivery. Keep
`FileConsentCard` only as a Teams-only enhancement (e.g. if you specifically want to push the
file into the user's OneDrive).

## Two commands in the sample
- `/sendlink` — sends a **markdown download link**. Works in **both** Teams and Copilot.
- `/sendfile` — sends a Teams **`FileConsentCard`**; on accept, the bot uploads the bytes to
  the user's OneDrive. **Teams only** (the sample detects the surface and, in Copilot, replies
  with a short note instead).

## The two gotchas that bite people

### 1. Teams `FileConsentCard` requires `"supportsFiles": true` in the manifest
If the bot entry in your Teams app manifest does **not** set `"supportsFiles": true`, clicking
**Allow** on the consent card fails with **"This card action is not supported by &lt;bot&gt;"**,
and the `fileConsent/invoke` **never reaches your bot**. See `manifest-snippet.json`. After
changing the manifest, bump the `version` and re-upload/update the app.

### 2. Embedded M365 Copilot does not support `FileConsentCard`
There is no file-consent/OneDrive-upload path in the embedded Copilot surface. Detect the
surface (see `Surface(...)` in the sample) and fall back to a **download link** there.

## Implementation notes (Microsoft.Agents SDK 1.5.x)
- The SDK has **no `FileConsentCard`/`FileInfoCard` type** — the card and the
  `fileConsent/invoke` accept/decline handler are **hand-rolled** against the raw Teams schema
  (see `FileDownloadSamples.cs`). `IInputFileDownloader` is for *inbound* files only.
- Avoid `FileInfoCard` for the post-upload confirmation — it can render as an *"unsupported
  card"* on some clients; a plain-text confirmation is reliable.
- **Whitespace-tolerant routing**: Teams can prefix a non-breaking space to the message text,
  which breaks exact-match command routing — match with a trimmed `StartsWith`.
- Acknowledge the `fileConsent/invoke` with an HTTP 200 `InvokeResponse` so Teams clears the
  spinner.

## Wiring it up
See the header comment in `FileDownloadSamples.cs` for the exact `OnActivity` registrations
(message routing + the scoped `fileConsent/invoke` handler). Host the file yourself via an
anonymous endpoint that streams the bytes (with `Content-Disposition: attachment`) and pass
its base URL as `publicBaseUrl`.

## Files
| File | Purpose |
|---|---|
| `FileDownloadSamples.cs` | The two commands + the file-consent invoke handler + surface detection |
| `manifest-snippet.json` | The `supportsFiles: true` bot entry to add to your Teams manifest |
