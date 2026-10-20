using ACopy.Api.Destinations;
using ACopy.Api.Data;
using ACopy.Api.Docker;
using ACopy.Api.Services;

namespace ACopy.Tests;

public class TelegramTests
{
    [Fact]
    public void Chat_lists_are_split_trimmed_and_deduplicated()
    {
        Assert.Equal(["-1001234567890", "42", "@backups"], TelegramClient.ParseChats(" -1001234567890, 42,,@backups ,42"));
        Assert.Empty(TelegramClient.ParseChats(null));
        Assert.Empty(TelegramClient.ParseChats(" , "));
    }

    [Theory]
    [InlineData("42", true)]
    [InlineData("-1001234567890", true)]
    [InlineData("@team_backups", true)]
    [InlineData("@", false)]
    [InlineData("bob", false)]
    [InlineData("@bad-name", false)]
    public void Chat_ids_are_numbers_or_public_usernames(string chat, bool valid) =>
        Assert.Equal(valid, TelegramClient.IsValidChat(chat));

    [Fact]
    public void Remote_ref_round_trips_per_chat()
    {
        var map = new Dictionary<string, List<long>> { ["-100123"] = [5, 6], ["42"] = [7], ["@empty"] = [] };
        var formatted = TelegramRef.Format(map);
        Assert.Equal("-100123:5,6;42:7", formatted);
        var parsed = TelegramRef.Parse(formatted, legacyChat: "unused");
        Assert.Equal([5L, 6L], parsed["-100123"]);
        Assert.Equal([7L], parsed["42"]);
        Assert.Equal(2, parsed.Count);
    }

    [Fact]
    public void Legacy_remote_ref_belongs_to_the_destination_chat()
    {
        var parsed = TelegramRef.Parse("1001,1002", legacyChat: "-100999");
        Assert.Equal([1001L, 1002L], Assert.Single(parsed).Value);
        Assert.Equal("-100999", parsed.Keys.Single());
        Assert.Empty(TelegramRef.Parse("1001", legacyChat: null));
        Assert.Empty(TelegramRef.Parse(null, "42"));
    }

    [Fact]
    public void Public_server_is_recognised()
    {
        Assert.True(TelegramClient.IsPublic("https://api.telegram.org/"));
        Assert.False(TelegramClient.IsPublic("http://telegram-bot-api:8081"));
    }

    [Fact]
    public void Upload_folder_retention_counts_parts_as_one_copy()
    {
        var now = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
        FileEntry F(string n, int daysAgo) => new(n, now.AddDays(-daysAgo));
        var files = new[]
        {
            F("db_20261020-030000.sql.gz.001", 0), F("db_20261020-030000.sql.gz.002", 0),
            F("db_20261019-030000.sql.gz.001", 1), F("db_20261019-030000.sql.gz.002", 1), F("db_20261019-030000.sql.gz.003", 1),
            F("db_20261018-030000.sql.gz", 2),
            F("other_20261001-030000.sql.gz", 19), F(".syncthing.acopy-db_20261020-030000.sql.gz.003.tmp", 0),
        };
        var prune = BackupFiles.PartedFilesToPrune("db", NamingMode.Timestamp, new RetentionPolicy(2, null), files, "db_20261020-030000.sql.gz", now);
        Assert.Equal(["db_20261018-030000.sql.gz"], prune);

        prune = BackupFiles.PartedFilesToPrune("db", NamingMode.Timestamp, new RetentionPolicy(1, null), files, "db_20261020-030000.sql.gz", now);
        Assert.Equal(["db_20261019-030000.sql.gz.001", "db_20261019-030000.sql.gz.002", "db_20261019-030000.sql.gz.003", "db_20261018-030000.sql.gz"], prune);
    }

    [Fact]
    public void Rolling_names_get_the_backup_date_for_remote_copies()
    {
        var rolling = new BackupTarget { Name = "App DB", Folder = "app-db", Naming = NamingMode.Rolling };
        var stamped = new BackupTarget { Name = "App DB", Folder = "app-db", Naming = NamingMode.Timestamp };
        var at = new DateTime(2026, 10, 20, 3, 0, 5);
        Assert.Equal("app-db_20261020-030005.sql.gz.age", BackupFiles.TimestampedName(rolling, "app-db.4.sql.gz.age", at));
        Assert.Equal("app-db_20261019-030000.sql.gz", BackupFiles.TimestampedName(stamped, "app-db_20261019-030000.sql.gz", at));
        Assert.Equal("unrelated.bin", BackupFiles.TimestampedName(rolling, "unrelated.bin", at));
    }

    [Fact]
    public void File_and_parts_are_found_by_name()
    {
        Assert.Equal(["db.0.sql.gz", "db.0.sql.gz.001", "db.0.sql.gz.012"],
            BackupFiles.FileAndParts("db.0.sql.gz", ["db.0.sql.gz", "db.0.sql.gz.001", "db.0.sql.gz.012", "db.1.sql.gz", "db.0.sql.gz.1", "db.0.sql.gz.0001"]));
    }

    [Fact]
    public void Labels_set_telegram_chats_per_job()
    {
        var (cfgs, errs) = LabelConfig.Parse("acopy", new Dictionary<string, string>
        {
            ["acopy.enable"] = "true", ["acopy.destinations"] = "tg", ["acopy.telegram_chats"] = "42",
            ["acopy.jobs.app.database"] = "app", ["acopy.jobs.app.telegram_chats"] = "111, 222,111",
            ["acopy.jobs.all.schedule"] = "0 4 * * *",
        }, "pg", "postgres:17");
        Assert.Empty(errs);
        Assert.Equal("111,222", cfgs.Single(c => c.Name == "pg-app").TelegramChats);
        Assert.Equal("42", cfgs.Single(c => c.Name == "pg-all").TelegramChats);
    }

    [Fact]
    public void Labels_reject_a_bad_chat_id()
    {
        var (cfgs, errs) = LabelConfig.Parse("acopy", new Dictionary<string, string>
        {
            ["acopy.enable"] = "true", ["acopy.telegram_chats"] = "42, alice",
        }, "pg", "postgres:17");
        Assert.Empty(cfgs);
        Assert.Contains(errs, e => e.Contains("alice"));
    }
}
