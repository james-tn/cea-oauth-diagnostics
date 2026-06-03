# OAuth Diagnostics Drop-in for Microsoft.Agents.Builder CEAs

A single-file diagnostic helper for Custom Engine Agents built on
`Microsoft.Agents.Builder`. Purpose: capture enough live data from a broken
sign-in session to pinpoint whether the failure is in the SDK, the bot
config, BF Token Service, or the Copilot channel.

**No new NuGet dependencies.** Uses only packages a CEA project already references.

## What you get

| Diagnostic | What question it answers |
|---|---|
| `/diag` chat command | What does the live OAuth state look like *right now* in this conversation? (Activity ChannelId, `FlowState` from storage, BF Token Service token state, whether the `ChannelIdIncludesProduct=false` workaround is active.) |
| Sign-in failure logger | Are sign-in attempts failing silently? Logs every `UserAuthorization` sign-in failure with its `Cause` + `Error.Message`. |
| Inbound HTTP activity logger | Does the bot endpoint actually receive the `signin/tokenExchange` invoke / `tokens/response` event after the user clicks Sign In? **This is the smoking-gun question for "frozen after login".** |
| OnTurnError logger | Are unhandled exceptions during invoke processing being swallowed? |

All diagnostic output is tagged `[OAUTH-DIAG]` in your logs so it's easy to filter.

## Install — three changes

### 1. Add `OAuthDiagnostics.cs` to your project

Drop it anywhere in your project tree. Namespace is `CeaOAuthDiagnostics` — adjust if it clashes with anything.

### 2. In your `Program.cs`

```csharp
using CeaOAuthDiagnostics;

// ... existing builder setup ...

// Register the inbound HTTP logger middleware
builder.Services.AddTransient<OAuthDiagnostics.InboundActivityLoggerMiddleware>();

var app = builder.Build();

// ... existing pipeline setup ...

// Add the inbound logger to the HTTP pipeline BEFORE MapAgentApplicationEndpoints
app.UseMiddleware<OAuthDiagnostics.InboundActivityLoggerMiddleware>();

// ... your existing app.MapAgentApplicationEndpoints(...) etc.
app.Run();
```

### 3. In your `AgentApplication`-derived class constructor

```csharp
public MyAgent(
    AgentApplicationOptions options,
    ILogger<MyAgent> log,
    IConfiguration config,
    IStorage storage)               // ← add IStorage if you don't already inject it
    : base(options)
{
    // ... your existing setup ...

    OAuthDiagnostics.Register(
        app:            this,
        storage:        storage,
        handlerName:    "<your-handler-name>",        // matches AgentApplication:UserAuthorization:DefaultHandlerName
                                                       // in appsettings.json
        connectionName: "<your-oauth-connection-name>", // the AzureBotOAuthConnectionName for your handler
        logger:         log);
}
```

That's it. No other changes required.

> **Optional forward-compatible variant.** `OAuthDiagnostics.Register` installs the `OnTurnError` logger via `app.Options.Adapter`, which is marked `[Obsolete]` in newer SDK versions (the warning is suppressed inside `OAuthDiagnostics.cs`). If you'd rather avoid even the suppressed warning, call `OAuthDiagnostics.InstallTurnErrorLogger(adapter, logger)` explicitly from `Program.cs` after resolving `IChannelAdapter` from DI — and the `Register(...)` call still works, it just won't auto-install that piece a second time.

## Reproduce + capture sequence

1. Deploy with this file in place, with `ProtocolJsonSerializer.ChannelIdIncludesProduct = false` still active.
2. Sign out (your `/logout` command) or use a fresh conversation thread.
3. Send your trigger message (e.g. **"Hello"**).
4. Click the sign-in card and complete sign-in.
5. **The moment the conversation appears frozen** (after login but before any bot reply), send **`/diag`** in the chat and copy the bot's reply.
6. Send your trigger message again, observe **"Invalid sign in code."**.
7. Send **`/diag`** again and copy the reply.
8. From your app logs, grab **all lines containing `OAUTH-DIAG`** between steps 3 and 7 (UTC timestamps preferred so we can correlate).
9. Send all three artifacts (two `/diag` outputs + log slice) back.

## What we're looking for in your output

- **After step 4 (post-login, frozen)**: did the inbound HTTP logger record any activity with `type=invoke name=signin/tokenExchange` or `type=event name=tokens/response`?
  - **If no** → the bug is upstream of your bot (BF / Copilot routing). Your bot is doing nothing wrong; sign-in completion is never being delivered to your endpoint.
  - **If yes but `OnTurnError` fired** → the exception in the stack trace will tell us which piece of the SDK or your code threw during the invoke.
  - **If yes and no `OnTurnError`** → the SDK processed it but didn't transition `FlowStarted=false`. The `/diag` output from step 5 will tell us why.
- **In `/diag` step 5**: is `FlowState` present with `FlowStarted=true` *after* a successful login? If yes, sign-in completion isn't clearing it. If no, the issue is elsewhere.
- **In `/diag` step 7**: same question, after the "Invalid sign in code" appears. Confirms whether the `OnContinueFlow` retry-loop is the path firing.

## Safety notes

- The `/diag` command never sends the BF token itself — only its length + expiration.
- The inbound HTTP logger logs activity *metadata* (type, name, channel id, conversation id, value-payload key names), **not** request bodies in full. If your activities ever carry secrets in `value`, you can adjust the `value_keys` line to drop that field.
- All diagnostic output goes through `ILogger` — wire up your usual log scrubbing if anything sensitive surfaces.
- Removing the diagnostics later is just: delete `OAuthDiagnostics.cs`, remove the three setup lines, redeploy.

## Known co-existence behavior

- **`/diag` route shadowing:** if your host already registers an `OnMessage("/diag", ...)` handler before calling `OAuthDiagnostics.Register(...)`, the host's handler wins (the SDK matches the first-registered route by rank). The helper's `/diag` is silently shadowed — no startup error, no warning. If you want the helper's `/diag` to take effect in a host with a pre-existing one, either remove the host's registration or rename one of them (e.g. change the helper's command to `/oauthdiag` in `OAuthDiagnostics.cs`).
- **`OnUserSignInFailure` is a single-delegate slot:** if your host calls `UserAuthorization.OnUserSignInFailure(...)` after `OAuthDiagnostics.Register(...)`, the host's delegate replaces the helper's. To get both, wrap your existing handler so it also calls the helper's (or call `Register(...)` last).
- **`OnTurnError` chaining is preserved:** `InstallTurnErrorLogger` (and the in-line install inside `Register`) wraps any existing `OnTurnError` and calls it after logging, so this one composes safely with whatever the host already had.
- **Verified working:** all four diagnostics run successfully end-to-end against `Microsoft.Agents.Builder 1.5.184` with synthetic activities (plain `message`, `invoke signin/tokenExchange`, `event tokens/response`, malformed JSON, non-POST). No SDK warnings or errors at startup or runtime.
