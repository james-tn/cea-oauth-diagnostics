// Self-contained file-download sample for Microsoft 365 Agents (Bot Framework) bots.
//
// Demonstrates two ways to deliver a file to a user and what works on each surface:
//
//   /sendlink  — a markdown download link. PORTABLE: works in BOTH native Teams
//                and embedded M365 Copilot (it's just a link the host opens).
//                This is the recommended cross-surface mechanism.
//
//   /sendfile  — a Teams FileConsentCard (upload to the user's OneDrive).
//                TEAMS-CHANNEL ONLY. Requires "supportsFiles": true on the bot in
//                the Teams app manifest (see manifest-snippet.json) — without it,
//                clicking Allow fails with "This card action is not supported"
//                because Teams never sends the fileConsent/invoke to the bot.
//                Embedded M365 Copilot does NOT support FileConsentCard at all.
//
// Notes:
//  * The Microsoft.Agents SDK (1.5.x) has no FileConsentCard/FileInfoCard type, so
//    the card and the fileConsent/invoke handler are hand-rolled against the raw
//    Teams schema. We avoid FileInfoCard (it can render as an "unsupported card")
//    and confirm the upload with plain text instead.
//  * Wire-up (in your AgentApplication subclass constructor):
//
//      // whitespace-tolerant routing (Teams may prefix a non-breaking space):
//      OnActivity(ActivityTypes.Message, (ctx, state, ct) =>
//      {
//          var t = (ctx.Activity.Text ?? "").Trim();
//          if (t.StartsWith("/sendlink", StringComparison.OrdinalIgnoreCase))
//              return FileDownloadSamples.SendLinkAsync(ctx, publicBaseUrl, externalUrl, ct);
//          if (t.StartsWith("/sendfile", StringComparison.OrdinalIgnoreCase))
//              return FileDownloadSamples.SendFileConsentAsync(ctx, ct);
//          return Task.CompletedTask;
//      }, rank: RouteRank.Last);
//
//      // handle ONLY the Teams file-consent invoke (scoped so it never shadows
//      // the SDK's OAuth signin/tokenExchange invokes):
//      OnActivity(
//          (ctx, _) => Task.FromResult(
//              ctx.Activity.IsType(ActivityTypes.Invoke)
//              && string.Equals(ctx.Activity.Name, "fileConsent/invoke",
//                               StringComparison.OrdinalIgnoreCase)),
//          (ctx, state, ct) => FileDownloadSamples.HandleFileConsentInvokeAsync(ctx, ct),
//          rank: RouteRank.First);
//
//  * Host the file yourself (e.g. an anonymous endpoint that streams the bytes with
//    Content-Disposition: attachment) and pass its base URL as `publicBaseUrl`.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Core.Models;

namespace TeamsCopilotFileDownload;

public static class FileDownloadSamples
{
    private static readonly HttpClient _http = new();
    private const string SampleFileName = "employees-sample.csv";

    // The bytes delivered in both patterns. Replace with your real file source.
    public static byte[] SampleCsvBytes() => Encoding.UTF8.GetBytes(
        "id,name,title,department\n" +
        "1,Ada Lovelace,Principal Engineer,Engineering\n" +
        "2,Alan Turing,Distinguished Scientist,Research\n" +
        "3,Grace Hopper,VP Engineering,Engineering\n");

    // ---------------------------------------------------------------------
    // /sendlink — portable download link (Teams + Copilot)
    // A markdown link renders and is clickable on every surface, so it is the
    // most reliable cross-surface download mechanism. (Rich Adaptive Cards can
    // fail to render on some Teams clients — "go.skype.com/cards.unsupported".)
    // ---------------------------------------------------------------------
    public static async Task SendLinkAsync(
        ITurnContext ctx, string? publicBaseUrl, string? externalUrl, CancellationToken ct)
    {
        var lines = new List<string>
        {
            "**Download the sample file** (portable — works in Teams **and** M365 Copilot):",
        };
        if (!string.IsNullOrEmpty(publicBaseUrl))
            lines.Add($"- [{SampleFileName} (self-hosted)]({publicBaseUrl}/files/sample.csv)");
        if (!string.IsNullOrEmpty(externalUrl))
            lines.Add($"- [sample file (external)]({externalUrl})");

        if (lines.Count == 1)
        {
            await ctx.SendActivityAsync(
                "No download URL configured. Provide a publicBaseUrl (your bot's public URL) " +
                "or an externalUrl, then retry /sendlink.", cancellationToken: ct);
            return;
        }
        await ctx.SendActivityAsync(string.Join("\n\n", lines), cancellationToken: ct);
    }

    // ---------------------------------------------------------------------
    // /sendfile — Teams FileConsentCard (Teams channel only)
    // ---------------------------------------------------------------------
    public static async Task SendFileConsentAsync(ITurnContext ctx, CancellationToken ct)
    {
        var surface = Surface(ctx.Activity);
        if (!string.Equals(surface, "teams", StringComparison.OrdinalIgnoreCase))
        {
            // FileConsentCard doesn't render here; send only an explanatory note.
            await ctx.SendActivityAsync(
                $"`/sendfile` uses a Teams **FileConsentCard**, which is a Teams-channel feature. " +
                $"Current surface is `{surface}`, where it isn't supported — use `/sendlink` here instead.",
                cancellationToken: ct);
            return;
        }

        var attachment = new Attachment
        {
            ContentType = "application/vnd.microsoft.teams.card.file.consent",
            Name = SampleFileName,
            Content = new
            {
                description = "Sample employee export. Accept to save it to your OneDrive.",
                sizeInBytes = SampleCsvBytes().Length,
                acceptContext = new { filename = SampleFileName },
                declineContext = new { filename = SampleFileName },
            },
        };
        await ctx.SendActivityAsync(MessageFactory.Attachment(attachment), ct);
    }

    // Handles the Teams file-consent accept/decline invoke. Register with a
    // selector scoped to name == "fileConsent/invoke".
    public static async Task HandleFileConsentInvokeAsync(ITurnContext ctx, CancellationToken ct)
    {
        try
        {
            var value = JsonSerializer.Deserialize<JsonElement>(
                JsonSerializer.Serialize(ctx.Activity.Value));
            var action = value.TryGetProperty("action", out var a) ? a.GetString() : null;

            if (string.Equals(action, "accept", StringComparison.OrdinalIgnoreCase)
                && value.TryGetProperty("uploadInfo", out var upload))
            {
                var uploadUrl = upload.GetProperty("uploadUrl").GetString();
                var name = upload.TryGetProperty("name", out var n) ? n.GetString() : SampleFileName;

                var bytes = SampleCsvBytes();
                using var content = new ByteArrayContent(bytes);
                content.Headers.ContentLength = bytes.Length;
                content.Headers.ContentRange = new ContentRangeHeaderValue(0, bytes.Length - 1, bytes.Length);
                using var resp = await _http.PutAsync(uploadUrl, content, ct);
                resp.EnsureSuccessStatusCode();

                // Plain text (a FileInfoCard can render as an "unsupported card").
                await ctx.SendActivityAsync($"✅ Uploaded **{name}** to your OneDrive.", cancellationToken: ct);
            }
            else
            {
                await ctx.SendActivityAsync("File download was declined.", cancellationToken: ct);
            }
        }
        catch (Exception ex)
        {
            await ctx.SendActivityAsync($"File upload failed: {ex.Message}", cancellationToken: ct);
        }
        finally
        {
            // Acknowledge the invoke (HTTP 200) so Teams clears the spinner.
            await ctx.SendActivityAsync(
                new Activity { Type = ActivityTypes.InvokeResponse, Value = new InvokeResponse { Status = 200 } },
                ct);
        }
    }

    // Classifies the surface from the activity: "teams", "copilot", "m365:<app>",
    // or "non-teams:<channel>". The base channel for Teams AND M365 hosts is
    // "msteams"; channelData.productContext (e.g. "COPILOT") distinguishes them.
    private static string Surface(IActivity activity)
    {
        var channel = activity.ChannelId?.Channel;
        if (string.IsNullOrEmpty(channel)) return "unknown";
        if (!string.Equals(channel, "msteams", StringComparison.OrdinalIgnoreCase))
            return $"non-teams:{channel}";

        string? product = null;
        try
        {
            if (activity.ChannelData is not null)
            {
                var je = JsonSerializer.Deserialize<JsonElement>(
                    JsonSerializer.Serialize(activity.ChannelData));
                if (je.ValueKind == JsonValueKind.Object
                    && je.TryGetProperty("productContext", out var pc)
                    && pc.ValueKind == JsonValueKind.String)
                {
                    product = pc.GetString();
                }
            }
        }
        catch { /* best effort */ }
        product ??= activity.ChannelId?.SubChannel;

        if (string.IsNullOrEmpty(product)) return "teams";
        return product.Equals("COPILOT", StringComparison.OrdinalIgnoreCase)
            ? "copilot"
            : $"m365:{product.ToLowerInvariant()}";
    }
}
