using System.Diagnostics;
using Dispatch.Core.Logging;
using Dispatch.Core.Providers;
using Dispatch.Core.Relays;
using Dispatch.Core.Routing;
using Dispatch.Web.Ingestion;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using MimeKit;

namespace Dispatch.Web.Endpoints;

/// <summary>Named-relay management and routing-rule endpoints (spec §10.10). Mapped under the web-port "/api" group.</summary>
public static class RoutingEndpoints
{
    public static void MapRelayRouting(this RouteGroupBuilder group)
    {
        MapRelays(group);
        MapRoutingRules(group);
    }

    private static void MapRelays(RouteGroupBuilder group)
    {
        group.MapGet("/relays", async (IRelayRepository relays, IRelaySettingsStore store, CancellationToken ct) =>
        {
            var items = new List<object>();
            foreach (var r in await relays.GetAllAsync(ct))
            {
                var s = await store.GetAsync(r.Id, ct);   // config is the source of truth for provider
                items.Add(new { r.Id, r.Name, provider = s.Provider.ToString(), r.IsDefault, r.Enabled, r.MaxConcurrency });
            }
            return Results.Ok(items);
        });

        group.MapGet("/relays/{id:int}", async (int id, IRelayRepository relays, IRelaySettingsStore store, CancellationToken ct) =>
        {
            var record = await relays.GetByIdAsync(id, ct);
            if (record is null) return Results.NotFound();
            return Results.Ok(await DetailAsync(record, store, ct));
        });

        group.MapPost("/relays", async (CreateRelayRequest req, IRelayRepository relays, IRelaySettingsStore store, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new { error = "Name is required." });
            if (!Enum.TryParse<RelayProviderType>(req.Provider, ignoreCase: true, out var provider))
                return Results.BadRequest(new { error = $"Unknown provider '{req.Provider}'." });

            var record = await relays.CreateAsync(req.Name, provider, req.MaxConcurrency ?? 4, 0, ct);
            await store.SaveAsync(record.Id, new RelaySettings(provider, new Dictionary<string, string?>()), ct);
            return Results.Ok(new { record.Id, record.Name, provider = provider.ToString() });
        });

        group.MapPut("/relays/{id:int}", async (int id, UpdateRelayRequest req, IRelayRepository relays, IRelaySettingsStore store, CancellationToken ct) =>
        {
            var record = await relays.GetByIdAsync(id, ct);
            if (record is null) return Results.NotFound();
            if (!Enum.TryParse<RelayProviderType>(req.Provider, ignoreCase: true, out var provider))
                return Results.BadRequest(new { error = $"Unknown provider '{req.Provider}'." });

            var existing = await store.GetAsync(id, ct);
            var settings = new Dictionary<string, string?>();
            foreach (var f in RelayProviderSchema.For(provider))
            {
                var provided = req.Settings is not null && req.Settings.TryGetValue(f.Name, out var v) ? v : null;
                settings[f.Name] = f.Secret && (string.IsNullOrEmpty(provided) || provided == "********")
                    ? (provider == existing.Provider ? existing.Settings.GetValueOrDefault(f.Name) : null)
                    : provided;
            }
            foreach (var f in RelayProviderSchema.For(provider).Where(f => f.Required))
                if (string.IsNullOrWhiteSpace(settings.GetValueOrDefault(f.Name)))
                    return Results.BadRequest(new { error = $"{f.Name} is required for {provider}." });

            await relays.UpdateAsync(id, req.Name ?? record.Name, provider, req.Enabled ?? record.Enabled,
                req.MaxConcurrency ?? record.MaxConcurrency, record.MaxMessageBytes, ct);
            await store.SaveAsync(id, new RelaySettings(provider, settings), ct);
            return Results.Ok(new { ok = true });
        });

        group.MapPost("/relays/{id:int}/set-default", async (int id, IRelayRepository relays, CancellationToken ct) =>
            await relays.SetDefaultAsync(id, ct) ? Results.Ok(new { ok = true }) : Results.NotFound());

        group.MapDelete("/relays/{id:int}", async (int id, IRelayRepository relays, IRoutingRuleRepository rules, CancellationToken ct) =>
        {
            var record = await relays.GetByIdAsync(id, ct);
            if (record is null) return Results.NotFound();
            if (record.IsDefault) return Results.BadRequest(new { error = "The default relay cannot be deleted." });
            if (await rules.CountReferencingRelayAsync(id, ct) > 0)
                return Results.BadRequest(new { error = "Relay is referenced by one or more routing rules." });

            return await relays.DeleteAsync(id, ct) ? Results.Ok(new { ok = true }) : Results.NotFound();
        });

        group.MapPost("/relays/{id:int}/test", async (
            int id, WebEndpoints.TestRelayRequest req, IRelayRepository relays, IRelaySettingsStore store,
            IRelayProviderFactory factory, ILogRepository log, Dispatch.Core.Configuration.ConfigCache cache, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.To)) return Results.BadRequest(new { error = "'to' is required." });
            var record = await relays.GetByIdAsync(id, ct);
            if (record is null) return Results.NotFound();
            var s = await store.GetAsync(id, ct);

            // Send the same spec §11.3-conformant message as the live provider test (proper From, timestamped
            // subject, text+HTML bodies, X-Dispatch-Test header) rather than an ad-hoc plaintext stub.
            var fromOverride = !string.IsNullOrWhiteSpace(req.From)
                ? req.From!
                : s.Provider == RelayProviderType.Mailgun && !string.IsNullOrEmpty(s.Settings.GetValueOrDefault("Domain"))
                    ? $"dispatch-test@{s.Settings["Domain"]}"
                    : null;

            var mime = Dispatch.Web.Realtime.ProviderTestService.BuildTestMessage(
                s.Provider, req.To, cache.Listener().ServerName, fromOverride);

            var config = new RelayConfig
            {
                Id = id, Name = record.Name, Provider = s.Provider,
                MaxConcurrency = record.MaxConcurrency, Settings = s.Settings,
            };
            var relayMessage = new RelayMessage { Message = mime, FromAddress = mime.From.Mailboxes.First().Address, ToAddresses = [req.To] };

            var sw = Stopwatch.StartNew();
            try
            {
                var provider = factory.Build(config);
                var result = await provider.SendAsync(relayMessage, ct);
                await log.InsertAsync(WebEndpoints.TestEntry("OK", id, config.Name, provider.Name, relayMessage,
                    mime.Subject, (int)sw.ElapsedMilliseconds, result.ProviderMessageId, result.ProviderDetail, null), ct);
                return Results.Ok(new { ok = true, provider = provider.Name, providerMessageId = result.ProviderMessageId, detail = result.ProviderDetail });
            }
            catch (Exception ex)
            {
                await log.InsertAsync(WebEndpoints.TestEntry("Error", id, config.Name, s.Provider.ToString(), relayMessage,
                    mime.Subject, (int)sw.ElapsedMilliseconds, null, null, ex.Message), ct);
                return Results.Ok(new { ok = false, error = ex.Message });
            }
        });
    }

    private static void MapRoutingRules(RouteGroupBuilder group)
    {
        group.MapGet("/routing/rules", async (IRoutingRuleRepository rules, IRelayRepository relays, CancellationToken ct) =>
        {
            var all = await rules.GetAllAsync(ct);
            var relayNames = (await relays.GetAllAsync(ct)).ToDictionary(r => r.Id, r => r.Name);
            return Results.Ok(all.Select(r => new
            {
                r.Id, r.Priority, r.Name, r.RecipientPattern, r.SenderPattern, r.RelayId,
                relayName = relayNames.GetValueOrDefault(r.RelayId, "(deleted)"), r.Enabled,
            }));
        });

        group.MapPost("/routing/rules", async (RuleRequest req, IRoutingRuleRepository rules, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new { error = "Name is required." });
            if (string.IsNullOrWhiteSpace(req.RecipientPattern) && string.IsNullOrWhiteSpace(req.SenderPattern))
                return Results.BadRequest(new { error = "At least one of recipientPattern or senderPattern is required." });

            var created = await rules.CreateAsync(new RoutingRule
            {
                Name = req.Name, RecipientPattern = Empty(req.RecipientPattern), SenderPattern = Empty(req.SenderPattern),
                RelayId = req.RelayId, Enabled = req.Enabled ?? true, Priority = req.Priority ?? 0,
            }, ct);
            return Results.Ok(new { created.Id, created.Priority });
        });

        group.MapPut("/routing/rules/{id:int}", async (int id, RuleRequest req, IRoutingRuleRepository rules, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.RecipientPattern) && string.IsNullOrWhiteSpace(req.SenderPattern))
                return Results.BadRequest(new { error = "At least one of recipientPattern or senderPattern is required." });
            var ok = await rules.UpdateAsync(new RoutingRule
            {
                Id = id, Name = req.Name, RecipientPattern = Empty(req.RecipientPattern), SenderPattern = Empty(req.SenderPattern),
                RelayId = req.RelayId, Enabled = req.Enabled ?? true,
            }, ct);
            return ok ? Results.Ok(new { ok = true }) : Results.NotFound();
        });

        group.MapDelete("/routing/rules/{id:int}", async (int id, IRoutingRuleRepository rules, CancellationToken ct) =>
            await rules.DeleteAsync(id, ct) ? Results.Ok(new { ok = true }) : Results.NotFound());

        group.MapPut("/routing/rules/reorder", async (ReorderRequest req, IRoutingRuleRepository rules, CancellationToken ct) =>
        {
            await rules.ReorderAsync(req.Ids, ct);
            return Results.Ok(new { ok = true });
        });

        group.MapPost("/routing/simulate", async (SimulateRequest req, IRelayResolver resolver, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.From) || string.IsNullOrWhiteSpace(req.To))
                return Results.BadRequest(new { error = "'from' and 'to' are required." });
            var resolved = await resolver.ResolveAsync(req.From, [req.To], ct);
            return Results.Ok(new
            {
                matched = resolved.RoutingMatched,
                resolved.MatchedRuleId,
                resolved.MatchedRuleName,
                relayId = resolved.Id,
                relayName = resolved.Name,
                provider = resolved.Config.Provider.ToString(),
            });
        });
    }

    private static async Task<object> DetailAsync(RelayRecord record, IRelaySettingsStore store, CancellationToken ct)
    {
        var s = await store.GetAsync(record.Id, ct);   // config is the source of truth for provider + fields
        var fields = RelayProviderSchema.For(s.Provider).Select(f =>
        {
            var hasValue = !string.IsNullOrEmpty(s.Settings.GetValueOrDefault(f.Name));
            return new
            {
                name = f.Name, secret = f.Secret, required = f.Required, hasValue,
                value = f.Secret ? (hasValue ? "********" : "") : (s.Settings.GetValueOrDefault(f.Name) ?? ""),
            };
        });
        return new
        {
            record.Id, record.Name, provider = s.Provider.ToString(), record.IsDefault, record.Enabled,
            record.MaxConcurrency, providers = SelectableProviders, fields,
        };
    }

    // "Unconfigured" is the implicit default, not something an admin picks from the dropdown.
    private static readonly string[] SelectableProviders =
        Enum.GetNames<RelayProviderType>().Where(n => n != nameof(RelayProviderType.Unconfigured)).ToArray();

    private static string? Empty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private sealed record CreateRelayRequest(string Name, string Provider, int? MaxConcurrency);
    private sealed record UpdateRelayRequest(string? Name, string Provider, bool? Enabled, int? MaxConcurrency, Dictionary<string, string?>? Settings);
    private sealed record RuleRequest(string Name, string? RecipientPattern, string? SenderPattern, int RelayId, bool? Enabled, int? Priority);
    private sealed record ReorderRequest(int[] Ids);
    private sealed record SimulateRequest(string From, string To);
}
