using System.Text.Json;
using ACopy.Api.Data;
using ACopy.Api.Destinations;
using ACopy.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace ACopy.Api.Endpoints;

/// <summary>Encryption keys, destinations, restores and upload retries.</summary>
public static class StorageEndpoints
{
    public static void MapStorageEndpoints(this RouteGroupBuilder api)
    {
        MapKeys(api.MapGroup("/keys"));
        MapDestinations(api.MapGroup("/destinations"));

        api.MapPost("/runs/{id:long}/restore", async (long id, RestoreInput input, AppDbContext db, BackupRunner runner) =>
        {
            var source = await db.Runs.Include(r => r.Target).FirstOrDefaultAsync(r => r.Id == id);
            if (source?.Target == null) return Results.NotFound();
            if (source.Kind != RunKind.Backup || source.Status != RunStatus.Succeeded) return Results.Problem("Only successful backups can be restored.", statusCode: 400);
            if (!string.Equals(input.ConfirmName?.Trim(), source.Target.Name, StringComparison.Ordinal))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["confirmName"] = ["Type the database name exactly to confirm"] });
            if (Engines.RestoreCommandBuilder.UnsupportedReason(source.Target) is { } reason) return Results.Problem(reason, statusCode: 400);
            var runId = await runner.StartRestoreAsync(id, new RestoreOptions(input.SafetyBackup, input.AgeIdentity, input.FromUploadId));
            return runId == null ? Results.Problem("This database is busy — wait for the running job to finish.", statusCode: 409) : Results.Ok(new { runId });
        });

        api.MapPost("/uploads/{id:long}/retry", async (long id, AppDbContext db, BackupRunner runner, CancellationToken ct) =>
        {
            var u = await db.Uploads.Include(x => x.Run).FirstOrDefaultAsync(x => x.Id == id, ct);
            if (u?.Run == null) return Results.NotFound();
            if (runner.IsActive(u.Run.TargetId)) return Results.Problem("This database is busy.", statusCode: 409);
            try
            {
                var done = await runner.RetryUploadAsync(id, ct);
                return Results.Ok(UploadDto.From(done));
            }
            catch (InvalidOperationException e) { return Results.Problem(e.Message, statusCode: 400); }
        });
    }

    // ── Keys ──

    private static void MapKeys(RouteGroupBuilder g)
    {
        g.MapGet("/", async (AppDbContext db) =>
        {
            var used = await db.Targets.Where(t => t.EncryptionKeyId != null).GroupBy(t => t.EncryptionKeyId!.Value)
                .Select(x => new { x.Key, Count = x.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);
            var keys = await db.Keys.AsNoTracking().OrderBy(k => k.Name).ToListAsync();
            return keys.Select(k => ToDto(k, used.GetValueOrDefault(k.Id)));
        });

        g.MapPost("/", async (KeyInput input, AppDbContext db, Secrets secrets, CancellationToken ct) =>
        {
            var errors = new Dictionary<string, string[]>();
            var name = input.Name?.Trim() ?? "";
            if (name.Length == 0 || name.Length > 60 || name.Contains(',')) errors["name"] = ["1–60 characters, no commas"];
            else if (await db.Keys.AnyAsync(k => k.Name == name, ct)) errors["name"] = ["A key with this name exists"];

            var key = new EncryptionKey { Name = name, Kind = input.Kind };
            if (input.Kind == KeyKind.Passphrase)
            {
                if (string.IsNullOrEmpty(input.Passphrase) || input.Passphrase.Length < 12) errors["passphrase"] = ["At least 12 characters"];
                else key.SecretProtected = secrets.Protect(input.Passphrase);
            }
            else if (input.Generate == true)
            {
                var (identity, recipient) = await Encryption.GenerateAgeKeyAsync(ct);
                key.SecretProtected = secrets.Protect(identity);
                key.Recipients = recipient;
            }
            else
            {
                var recipients = Encryption.ParseRecipients(input.Recipients);
                if (!string.IsNullOrWhiteSpace(input.Identity))
                {
                    var pub = await Encryption.RecipientOfIdentityAsync(input.Identity, ct);
                    if (pub == null) errors["identity"] = ["Not a valid age identity (AGE-SECRET-KEY-1…)"];
                    else
                    {
                        if (!recipients.Contains(pub)) recipients.Insert(0, pub);
                        key.SecretProtected = secrets.Protect(input.Identity.Trim() + "\n");
                    }
                }
                if (recipients.Count == 0) errors["recipients"] = ["At least one recipient (age1… or ssh-ed25519 …)"];
                else if (recipients.FirstOrDefault(r => !Encryption.IsValidRecipient(r)) is { } bad) errors["recipients"] = [$"Not an age recipient: {bad}"];
                key.Recipients = string.Join('\n', recipients);
            }
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            db.Keys.Add(key);
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(key, 0));
        });

        // The one place a secret leaves ACopy: the age private key, so it can be kept offline.
        g.MapPost("/{id:int}/export", async (int id, PasswordInput confirm, AppDbContext db, Secrets secrets, SettingsStore settings) =>
        {
            var auth = await settings.GetAsync(SettingsStore.KeyPasswordHash);
            if (auth != null && !SettingsStore.VerifyPassword(confirm.Password, auth)) return Results.Problem("Wrong password.", statusCode: 403);
            var k = await db.Keys.FindAsync(id);
            if (k == null) return Results.NotFound();
            if (k.Kind != KeyKind.Age || k.SecretProtected == null) return Results.Problem("Only age keys with a stored identity can be exported.", statusCode: 400);
            return Results.Text(secrets.Unprotect(k.SecretProtected)!, "text/plain");
        });

        g.MapDelete("/{id:int}", async (int id, AppDbContext db) =>
        {
            var k = await db.Keys.FindAsync(id);
            if (k == null) return Results.NotFound();
            if (await db.Targets.AnyAsync(t => t.EncryptionKeyId == id))
                return Results.Problem("Databases still use this key — switch them to another key first.", statusCode: 409);
            db.Keys.Remove(k);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }

    private static KeyDto ToDto(EncryptionKey k, int usedBy) => new(
        k.Id, k.Name, k.Kind.ToString(), k.SecretProtected != null, Encryption.ParseRecipients(k.Recipients), usedBy, k.CreatedAt,
        Encryption.DecryptHint(k.Kind));

    // ── Destinations ──

    private static void MapDestinations(RouteGroupBuilder g)
    {
        g.MapGet("/backends", () => RcloneCatalog.Backends);

        g.MapGet("/", async (AppDbContext db, DestinationService ds, Secrets secrets) =>
        {
            var used = await db.TargetDestinations.GroupBy(t => t.DestinationId)
                .Select(x => new { x.Key, Count = x.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);
            var list = await db.Destinations.AsNoTracking().OrderBy(d => d.Name).ToListAsync();
            return list.Select(d => ToDto(d, ds, secrets, used.GetValueOrDefault(d.Id)));
        });

        g.MapPost("/", async (DestinationInput input, AppDbContext db, DestinationService ds, Secrets secrets) =>
        {
            var d = new Destination { Name = "" };
            var errors = await ApplyAsync(d, input, db, secrets);
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            db.Destinations.Add(d);
            await db.SaveChangesAsync();
            return Results.Ok(ToDto(d, ds, secrets, 0));
        });

        g.MapPut("/{id:int}", async (int id, DestinationInput input, AppDbContext db, DestinationService ds, Secrets secrets) =>
        {
            var d = await db.Destinations.FindAsync(id);
            if (d == null) return Results.NotFound();
            var errors = await ApplyAsync(d, input, db, secrets);
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            await db.SaveChangesAsync();
            return Results.Ok(ToDto(d, ds, secrets, await db.TargetDestinations.CountAsync(t => t.DestinationId == id)));
        });

        g.MapDelete("/{id:int}", async (int id, AppDbContext db) =>
        {
            var d = await db.Destinations.FindAsync(id);
            if (d == null) return Results.NotFound();
            db.Destinations.Remove(d); // detaches it from targets; remote files stay where they are
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        g.MapPost("/{id:int}/test", async (int id, AppDbContext db, DestinationService ds, CancellationToken ct) =>
            await db.Destinations.FindAsync([id], ct) is { } d ? Results.Ok(await ds.TestAsync(d, ct)) : Results.NotFound());

        // Test unsaved form values; blank secrets fall back to the stored destination's.
        g.MapPost("/test", async (DestinationInput input, int? id, AppDbContext db, DestinationService ds, Secrets secrets, CancellationToken ct) =>
        {
            var d = id != null ? await db.Destinations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? new Destination { Name = "" } : new Destination { Name = "" };
            var errors = await ApplyAsync(d, input with { Name = "test-" + Guid.NewGuid().ToString("N") }, db, secrets);
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            return Results.Ok(await ds.TestAsync(d, ct));
        });

        // Telegram: the one-time logOut from api.telegram.org before the bot moves to a self-hosted Bot API server.
        // Same input as /test, so it works on unsaved form values (a blank token falls back to the stored one).
        g.MapPost("/telegram-logout", async (DestinationInput input, int? id, AppDbContext db, DestinationService ds, Secrets secrets, CancellationToken ct) =>
        {
            var d = id != null ? await db.Destinations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? new Destination { Name = "" } : new Destination { Name = "" };
            if (input.Kind != DestinationKind.Telegram) return Results.BadRequest();
            var errors = await ApplyAsync(d, input with { Name = "logout-" + Guid.NewGuid().ToString("N") }, db, secrets);
            if (errors.ContainsKey("secrets.botToken")) return Results.ValidationProblem(errors);
            return Results.Ok(await ds.LogOutFromPublicAsync(d, ct));
        });
    }

    private static async Task<Dictionary<string, string[]>> ApplyAsync(Destination d, DestinationInput i, AppDbContext db, Secrets secrets)
    {
        var e = new Dictionary<string, string[]>();
        var name = i.Name?.Trim() ?? "";
        if (name.Length == 0 || name.Length > 60 || name.Contains(',')) e["name"] = ["1–60 characters, no commas"];
        else if (await db.Destinations.AnyAsync(x => x.Name == name && x.Id != d.Id)) e["name"] = ["A destination with this name exists"];
        if (i.Keep is < 0 or > 10000) e["keep"] = ["0–10000"];
        if (i.MaxAgeDays is < 1 or > 36500) e["maxAgeDays"] = ["1–36500 days"];

        var p = (i.Params ?? []).Where(kv => !string.IsNullOrWhiteSpace(kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value.Trim());
        var stored = d.SecretsProtected == null ? new Dictionary<string, string>() : secrets.UnprotectMap(d.SecretsProtected);
        foreach (var (k, v) in i.Secrets ?? []) if (!string.IsNullOrEmpty(v)) stored[k] = v;
        foreach (var k in i.ClearSecrets ?? []) stored.Remove(k);

        if (i.Kind == DestinationKind.Rclone)
        {
            var backend = RcloneCatalog.Get(i.RcloneType);
            if (backend == null) e["rcloneType"] = ["Pick a backend"];
            else
            {
                var allowed = backend.Fields.Select(f => f.Key).ToHashSet();
                p = p.Where(kv => allowed.Contains(kv.Key)).ToDictionary();
                stored = stored.Where(kv => backend.Fields.Any(f => f.Secret && f.Key == kv.Key)).ToDictionary();
                foreach (var f in backend.Fields.Where(f => f.Required))
                    if (!(f.Secret ? stored.ContainsKey(f.Key) : p.ContainsKey(f.Key))) e[$"params.{f.Key}"] = [$"{f.Label} is required"];
            }
        }
        else
        {
            p = p.Where(kv => kv.Key is "chatId" or "threadId" or "apiUrl" or "maxPartMb").ToDictionary();
            stored = stored.Where(kv => kv.Key == "botToken").ToDictionary();
            var chats = TelegramClient.ParseChats(p.GetValueOrDefault("chatId"));
            if (chats.Count == 0) e["params.chatId"] = ["Chat id is required"];
            else if (chats.FirstOrDefault(c => !TelegramClient.IsValidChat(c)) is { } bad) e["params.chatId"] = [$"'{bad}' is not a chat id"];
            else p["chatId"] = string.Join(',', chats);
            if (p.TryGetValue("apiUrl", out var au) && !(Uri.TryCreate(au, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"))
                e["params.apiUrl"] = ["An http(s) URL, e.g. http://telegram-bot-api:8081"];
            if (!stored.ContainsKey("botToken")) e["secrets.botToken"] = ["Bot token is required"];
            if (p.TryGetValue("threadId", out var th) && !int.TryParse(th, out _)) e["params.threadId"] = ["A number"];
            if (p.TryGetValue("maxPartMb", out var mp) && (!int.TryParse(mp, out var mpv) || mpv is < 1 or > 2000)) e["params.maxPartMb"] = ["1–2000"];
        }
        if (e.Count > 0) return e;

        d.Name = name;
        d.Kind = i.Kind;
        d.Enabled = i.Enabled;
        d.RcloneType = i.Kind == DestinationKind.Rclone ? i.RcloneType : null;
        d.ParamsJson = JsonSerializer.Serialize(p);
        d.SecretsProtected = secrets.ProtectMap(stored);
        d.RemotePath = i.Kind == DestinationKind.Rclone ? i.RemotePath?.Trim() : null;
        d.Keep = i.Keep;
        d.MaxAgeDays = i.MaxAgeDays;
        return e;
    }

    private static DestinationDto ToDto(Destination d, DestinationService ds, Secrets secrets, int usedBy) => new(
        d.Id, d.Name, d.Kind.ToString(), d.Enabled, d.RcloneType, ds.Params(d),
        secrets.UnprotectMap(d.SecretsProtected).Keys.Order().ToList(), d.RemotePath, d.Keep, d.MaxAgeDays, usedBy, d.CreatedAt);
}
