using ACopy.Api.Data;
using Cronos;
using Microsoft.EntityFrameworkCore;

namespace ACopy.Api.Services;

/// <summary>
/// Fires scheduled backups. Missed occurrences (ACopy was down) are not caught up — the next
/// occurrence after start-up is the next run.
/// </summary>
public class Scheduler(IServiceScopeFactory scopes, BackupRunner runner, SettingsStore settings, ILogger<Scheduler> log) : BackgroundService
{
    private readonly Dictionary<int, (string Schedule, string Tz, DateTimeOffset? Due)> _due = [];

    public static CronExpression? TryParse(string? schedule)
    {
        if (string.IsNullOrWhiteSpace(schedule)) return null;
        var fields = schedule.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        try { return CronExpression.Parse(schedule.Trim(), fields == 6 ? CronFormat.IncludeSeconds : CronFormat.Standard); }
        catch (CronFormatException) { return null; }
    }

    public static DateTimeOffset? NextOccurrence(string schedule, DateTimeOffset from, TimeZoneInfo tz) =>
        TryParse(schedule)?.GetNextOccurrence(from, tz);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        do
        {
            try { await TickAsync(); }
            catch (Exception e) { log.LogError(e, "Scheduler tick failed"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task TickAsync()
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var targets = await db.Targets.AsNoTracking()
            .Where(t => t.Enabled && !t.Orphaned)
            .Select(t => new { t.Id, t.Schedule, t.Name })
            .ToListAsync();
        var tzId = await settings.GetTimeZoneIdAsync();
        var tz = await settings.GetTimeZoneAsync();
        var now = DateTimeOffset.UtcNow;

        foreach (var id in _due.Keys.Except(targets.Select(t => t.Id)).ToList()) _due.Remove(id);

        foreach (var t in targets)
        {
            if (!_due.TryGetValue(t.Id, out var entry) || entry.Schedule != t.Schedule || entry.Tz != tzId)
            {
                _due[t.Id] = (t.Schedule, tzId, NextOccurrence(t.Schedule, now, tz));
                continue;
            }
            if (entry.Due is not { } due || due > now) continue;

            _due[t.Id] = entry with { Due = NextOccurrence(t.Schedule, now, tz) };
            if (runner.IsActive(t.Id))
            {
                log.LogInformation("Skipping scheduled backup of {Name}: previous run still in progress", t.Name);
                continue;
            }
            log.LogInformation("Scheduled backup of {Name}", t.Name);
            await runner.StartAsync(t.Id, RunTrigger.Schedule);
        }
    }
}
