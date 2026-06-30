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

## Choosing an approach

There are two viable patterns. The decision is essentially **"do I want to host the bytes?"**
vs. **"do I need it to work in Copilot too?"**

| | **Download link** (`/sendlink`, `/sendcard`) | **`FileConsentCard`** (`/sendfile`) |
|---|---|---|
| You host the bytes | ✅ required (Azure Blob + SAS, or your own endpoint) | ❌ **not needed** — uploads to the **user's** OneDrive |
| Microsoft Graph permission / user token | not needed | **not needed** (Teams provides a pre-authorized upload URL) |
| Works in native Teams | ✅ | ✅ (requires `"supportsFiles": true`) |
| Works in **embedded M365 Copilot** | ✅ | ❌ not supported |
| User action | one click on the link | must click **Allow** on the consent card |
| Result | direct download | file saved to the user's OneDrive (bot gets a `contentUrl` link) |

- **Pick the download link** if you need it to work in **both** Teams and Copilot, or want a
  true "click → download". You just have to host the bytes — a short-lived, read-only Azure
  Blob **SAS** URL with `Content-Disposition: attachment` is the typical choice.
- **Pick the `FileConsentCard`** if you want **zero hosting** and only need **Teams** — the file
  is created in memory and pushed straight to the user's OneDrive.

### How the `FileConsentCard` upload works (no storage, no Graph)
1. The bot sends the consent card (file **name + size only** — no bytes yet).
2. The user clicks **Allow** → **Teams** provisions a location in the **user's OneDrive** and
   sends the bot a `fileConsent/invoke` containing a **pre-authorized `uploadInfo.uploadUrl`**.
3. The bot **PUTs the bytes** (generated **in memory**) to that URL — no Blob/SAS, and **no
   Graph permission or user token** required; the pre-authorized URL carries the write
   authorization.
4. The file now lives in the user's OneDrive; `uploadInfo.contentUrl` is the web link to it,
   which the bot can show the user (this sample returns it as a clickable markdown link).

Notes: cleanest in **1:1 / personal** chats; the user needs OneDrive for Business provisioned
(standard in M365); larger files use chunked `Content-Range` uploads.

## Commands in the sample
- `/sendlink` — a **markdown download link**. Works in **both** Teams and Copilot.
- `/sendcard` — an **Adaptive Card with a "Download" button** (`Action.OpenUrl` → **https** URL).
  Works in **both** Teams and Copilot. This is the correct version of the common broken pattern
  (see gotcha #3).
- `/sendfile` — a Teams **`FileConsentCard`**; on accept, the bot uploads the bytes to the
  user's OneDrive and returns a link. **Teams only** (the sample detects the surface and, in
  Copilot, replies with a short note instead).

## The gotchas that bite people

### 1. Teams `FileConsentCard` requires `"supportsFiles": true` in the manifest
If the bot entry in your Teams app manifest does **not** set `"supportsFiles": true`, clicking
**Allow** on the consent card fails with **"This card action is not supported by &lt;bot&gt;"**,
and the `fileConsent/invoke` **never reaches your bot**. See `manifest-snippet.json`. After
changing the manifest, bump the `version` and re-upload/update the app.

### 2. Embedded M365 Copilot does not support `FileConsentCard`
There is no file-consent/OneDrive-upload path in the embedded Copilot surface. Detect the
surface (see `Surface(...)` in the sample) and fall back to a **download link** there.

### 3. Don't put a `data:` URI in an Adaptive Card `Action.OpenUrl`
A tempting shortcut is to embed the file bytes directly in the card — binding an
`Action.OpenUrl` to a `data:<mime>;base64,…` URI. **This fails silently in native Teams
(desktop and mobile): clicking the button does nothing.** Teams' `Action.OpenUrl` only opens
**`http`/`https`** (and Teams deep-link) schemes — `data:`, `blob:` and `file:` are not
handled. It appears to work in Teams/M365 Copilot **web** only because the browser-based host
can open/download the `data:` URI.

Embedding base64 also inflates the activity payload, which Teams/Bot Framework caps at
**~28 KB**, so anything but a tiny file can fail to send entirely — independent of the scheme
issue.

**Fix:** persist the bytes and hand the button a real **`https://`** URL — e.g. an Azure Blob
**short-lived, read-only SAS** URL with `Content-Disposition: attachment; filename="…"` so it
triggers a true download on every client. A real `https` URL works in native Teams **and**
Copilot web, so both surfaces share one code path. (`/sendcard` is exactly this — the same
Adaptive Card `Action.OpenUrl` structure, but with an **`https`** url instead of `data:` — and
`/sendlink` is the plain-markdown equivalent.)

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
