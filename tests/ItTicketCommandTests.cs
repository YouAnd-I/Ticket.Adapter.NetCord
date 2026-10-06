using NetCord.Rest;
using Ticket.Adapter.NetCord;
using Ticket.Data;
using Xunit;

namespace Ticket.Adapter.NetCord.Tests;

public class ItTicketCommandTests
{
    private static readonly TicketView Full = new()
    {
        TicketId = "abcd1234",
        Exists = true,
        Title = "printer on fire",
        Description = "it smells",
        Priority = "no-rush",
        AutoClassified = true,
        CreatedAtUtc = DateTimeOffset.UtcNow.AddHours(-2),
        AttachmentUrl = "https://cdn.example/f.png",
        Assignee = "<@42>",
        NoteCount = 2,
        SolutionTitle = "Restart it",
        SolutionText = "Turn it off and on again.",
        SolutionImage = "https://cdn.example/s.png",
        Status = "open",
    };

    [Fact]
    public void Card_RendersEveryField_TheWayDiscordExpects()
    {
        var card = ItTicketCommands.Card(Full, "created", liveTimer: false);

        Assert.StartsWith("**IT ticket `abcd1234`** — created (open for ", card);
        Assert.Matches(@"^.*\(open for \d+[hms]", card);
        Assert.Contains("Title: **printer on fire**\n", card);
        Assert.Contains("Priority: `no-rush` *(auto-classified)*", card);
        Assert.Contains("\n> it smells", card);
        Assert.Contains("\n👤 Handled by <@42>", card);
        Assert.Contains("\n📎 https://cdn.example/f.png", card);
        Assert.Contains("\n📝 2 note(s)", card);
        Assert.Contains("\n\n**💡 IT solution — Restart it:**\nTurn it off and on again.\nhttps://cdn.example/s.png", card);
    }

    [Fact]
    public void Card_LiveTimer_MentionsTheCreationTimestamp()
    {
        var card = ItTicketCommands.Card(Full, "created", liveTimer: true);
        Assert.Contains($" — <t:{Full.CreatedAtUtc.ToUnixTimeSeconds()}:R>", card);
    }

    [Fact]
    public void Card_ClassifierOffline_GetsTheFallbackMark()
    {
        var offline = Full with { AutoClassified = false, ClassifierOffline = true };
        Assert.Contains("Priority: `no-rush` *(classifier offline — defaulted)*",
            ItTicketCommands.Card(offline, "created", liveTimer: false));
    }

    [Fact]
    public void Card_UnknownTicket_IsHeaderOnly()
    {
        var unknown = new TicketView { TicketId = "gone", Priority = "urgent", Status = "open" };
        Assert.Equal("**IT ticket `gone`** — status updated to `complete`",
            ItTicketCommands.Card(unknown, "status updated to `complete`", liveTimer: false));
    }

    [Fact]
    public void Rows_KeepTheCustomIdSchemesTheButtonsDispatchOn()
    {
        Assert.Equal(
            ["itstatus:cancel:t1", "itstatus:complete:t1", "itstatus:unsolved:t1", "itstatus:planned:t1", "itreport:t1"],
            ItTicketCommands.FullRow("t1").Components.OfType<ButtonProperties>().Select(b => b.CustomId));
        Assert.Equal(
            ["itreopen:t1", "itnote:t1", "itreport:t1"],
            ItTicketCommands.FollowupRow("t1").Components.OfType<ButtonProperties>().Select(b => b.CustomId));
    }
}
