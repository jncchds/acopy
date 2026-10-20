using ACopy.Api.Destinations;
using ACopy.Api.Docker;

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
