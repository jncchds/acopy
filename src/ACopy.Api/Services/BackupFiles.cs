using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ACopy.Api.Data;
using ACopy.Api.Engines;

namespace ACopy.Api.Services;

/// <summary>File naming, temp files and retention. Pure functions over a directory — no database.</summary>
public record RetentionPolicy(int Keep, int? MaxAgeDays);

public record FileEntry(string Name, DateTime ModifiedUtc);

public static partial class BackupFiles
{
    public const string TimestampFormat = "yyyyMMdd-HHmmss";

    /// <summary>
    /// In-progress files are named <c>.syncthing.acopy-…tmp</c>: Syncthing never syncs files matching its own
    /// temp-file pattern (<c>.syncthing.*.tmp</c>), so a half-written backup is never shipped to peers.
    /// The final rename is atomic (same directory).
    /// </summary>
    public const string TempPrefix = ".syncthing.acopy-";
    public const string TempSuffix = ".tmp";

    public static string Slug(string name)
    {
        var sb = new StringBuilder();
        foreach (var ch in name.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch is '-' or '.') sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var s = sb.ToString().Trim('-', '.');
        return s.Length == 0 ? "backup" : s;
    }

    /// <summary>Base name of the files inside the folder (slug of the target name).</summary>
    public static string BaseName(BackupTarget t) => Slug(t.Name);

    public static string FileName(BackupTarget t, DateTime localNow, int slot, KeyKind? encryption = null) => t.Naming switch
    {
        NamingMode.Rolling => $"{BaseName(t)}.{slot}.{EngineCatalog.FileExtension(t, encryption)}",
        _ => $"{BaseName(t)}_{localNow.ToString(TimestampFormat, CultureInfo.InvariantCulture)}.{EngineCatalog.FileExtension(t, encryption)}",
    };

    public static string TempName(string finalFileName) => $"{TempPrefix}{finalFileName}{TempSuffix}";

    /// <summary>Relative folder must stay inside the backups root.</summary>
    public static string SafeFolder(string folder)
    {
        var parts = folder.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0 && p != "." && p != "..")
            .Select(Slug);
        var joined = string.Join('/', parts);
        return joined.Length == 0 ? "backup" : joined;
    }

    /// <summary>
    /// Files that belong to this target and should be deleted to honour the retention policy. Only names this
    /// target would have produced are ever considered — anything else in the folder is left alone.
    /// Timestamp naming: beyond the newest <c>Keep</c>, or older than <c>MaxAgeDays</c>; the newest file is always kept.
    /// Rolling naming: slots ≥ Keep (Keep was lowered), or the written slot under an old extension.
    /// </summary>
    public static List<string> FilesToPrune(
        string baseName, NamingMode naming, RetentionPolicy policy, IEnumerable<FileEntry> files, string? justWritten, DateTime nowUtc)
    {
        var list = files.ToList();
        var escaped = Regex.Escape(baseName);

        if (naming == NamingMode.Timestamp)
        {
            var rx = new Regex($@"^{escaped}_(\d{{8}}-\d{{6}})\..+$");
            var ordered = list
                .Select(f => (f, m: rx.Match(f.Name)))
                .Where(x => x.m.Success)
                .OrderByDescending(x => x.m.Groups[1].Value, StringComparer.Ordinal)
                .ThenByDescending(x => x.f.Name == justWritten)
                .Select(x => x.f)
                .ToList();
            var result = new List<string>();
            for (var i = 1; i < ordered.Count; i++)
            {
                var beyondCount = policy.Keep > 0 && i >= policy.Keep;
                var tooOld = policy.MaxAgeDays is > 0 && nowUtc - ordered[i].ModifiedUtc > TimeSpan.FromDays(policy.MaxAgeDays.Value);
                if (beyondCount || tooOld) result.Add(ordered[i].Name);
            }
            return result;
        }
        else
        {
            var keep = Math.Max(1, policy.Keep);
            var rx = new Regex($@"^{escaped}\.(\d+)\..+$");
            var result = new List<string>();
            int? writtenSlot = null;
            if (justWritten != null && rx.Match(justWritten) is { Success: true } wm) writtenSlot = int.Parse(wm.Groups[1].Value);
            foreach (var f in list)
            {
                var m = rx.Match(f.Name);
                if (!m.Success || f.Name == justWritten) continue;
                if (!int.TryParse(m.Groups[1].Value, out var slot)) continue;
                if (slot >= keep || slot == writtenSlot) result.Add(f.Name);
            }
            return result;
        }
    }

    public static List<string> FilesToPrune(BackupTarget t, IEnumerable<FileEntry> files, string? justWritten, DateTime nowUtc) =>
        FilesToPrune(BaseName(t), t.Naming, new RetentionPolicy(t.Keep, t.MaxAgeDays), files, justWritten, nowUtc);

    public static List<FileEntry> ListFolder(string folder) =>
        Directory.Exists(folder)
            ? new DirectoryInfo(folder).EnumerateFiles().Select(f => new FileEntry(f.Name, f.LastWriteTimeUtc)).ToList()
            : [];

    /// <summary>Every backup file in the folder this target produced (any timestamp or slot, any extension).</summary>
    public static List<string> OwnedFiles(BackupTarget t, IEnumerable<string> fileNamesInFolder)
    {
        var baseName = Regex.Escape(BaseName(t));
        var rx = t.Naming == NamingMode.Timestamp
            ? new Regex($@"^{baseName}_\d{{8}}-\d{{6}}\..+$")
            : new Regex($@"^{baseName}\.\d+\..+$");
        return fileNamesInFolder.Where(n => rx.IsMatch(n)).ToList();
    }

    public static bool IsAcopyTemp(string fileName) =>
        fileName.StartsWith(TempPrefix, StringComparison.Ordinal) && fileName.EndsWith(TempSuffix, StringComparison.Ordinal);
}
