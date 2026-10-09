using Ecs.Client;
using Microsoft.Extensions.Hosting;
using NetCord;
using NetCord.Hosting.Services.ApplicationCommands;
using NetCord.Hosting.Services.ComponentInteractions;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using NetCord.Services.ComponentInteractions;
using Ticket.Data;

namespace Ticket.Adapter.NetCord;

public static partial class ItTicketCommands
{
    public enum PriorityOption
    {
        [SlashCommandChoice(Name = "auto")] Auto,
        [SlashCommandChoice(Name = "urgent")] Urgent,
        [SlashCommandChoice(Name = "no-rush")] NoRush,
    }

    public static void AddItTickets(this IHost host, IWorldClient world)
    {
        host.AddSlashCommand("it", "Create an IT ticket", (
            ApplicationCommandContext c,
            [SlashCommandParameter(Description = "Short summary")] string? title = null,
            [SlashCommandParameter(Description = "What happened?")] string? description = null,
            [SlashCommandParameter(Description = "How urgent is it?")] PriorityOption? priority = null,
            [SlashCommandParameter(Description = "Attach a screenshot or file")] Attachment? attachment = null,
            [SlashCommandParameter(Description = "Who should handle this (defaults to on-call IT)")] User? assignee = null) =>
            HandleItAsync(world, c, title, description, priority, attachment, assignee));

        host.AddComponentInteraction<ModalInteractionContext>("modal-it-ticket",
            (ModalInteractionContext c) => HandleModalAsync(world, c));

        host.AddComponentInteraction<ButtonInteractionContext>("itstatus",
            (ButtonInteractionContext c, string status, string ticketId) => HandleStatusAsync(world, c, status, ticketId));

        host.AddComponentInteraction<ButtonInteractionContext>("itreopen",
            (ButtonInteractionContext c, string ticketId) => HandleReopenAsync(world, c, ticketId));

        host.AddComponentInteraction<ButtonInteractionContext>("itnote",
            (ButtonInteractionContext c, string ticketId) =>
            InteractionCallback.Modal(new ModalProperties($"notemodal:{ticketId}", $"Note on ticket {ticketId}")
            {
                new LabelProperties("Follow-up note", new TextInputProperties("note", TextInputStyle.Paragraph)),
            }));

        host.AddComponentInteraction<ModalInteractionContext>("notemodal",
            (ModalInteractionContext c, string ticketId) => HandleNoteAsync(world, c, ticketId));

        host.AddComponentInteraction<ButtonInteractionContext>("itreport",
            (ButtonInteractionContext c, string ticketId) =>
            InteractionCallback.Modal(new ModalProperties($"reportmodal:{ticketId}", "Confidential Report")
            {
                new TextDisplayProperties("**This report is confidential** — it won't be shown publicly."),
                new LabelProperties("Your complaint", new TextInputProperties("complaint", TextInputStyle.Paragraph)),
                new LabelProperties("What action should have been taken?", new TextInputProperties("action", TextInputStyle.Paragraph)),
                new LabelProperties("Evidence", new FileUploadProperties("reportfile") { Required = false, MaxValues = 1 }),
                new LabelProperties("Stay anonymous", new CheckboxProperties("anonymous") { Default = true })
                { Description = "We won't attach your name to this report" },
            }));

        host.AddComponentInteraction<ModalInteractionContext>("reportmodal",
            (ModalInteractionContext c, string ticketId) => HandleReportAsync(world, c, ticketId));
    }

    public static Task<InteractionCallbackProperties> HandleItAsync(
        IWorldClient world, ApplicationCommandContext c,
        string? title, string? description, PriorityOption? priority, Attachment? attachment, User? assignee)
    {
        if (title is not null || description is not null || priority is not null || attachment is not null || assignee is not null)
        {
            var mapped = priority is null ? TicketPriority.Auto : Map(priority.Value);
            _ = Task.Run(() => CreateAsync(world, c.User, c.Client.Rest, c.Interaction,
                title, description, mapped, attachment?.Url, assignee?.Id));
            return Task.FromResult<InteractionCallbackProperties>(
                InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        }

        return Task.FromResult<InteractionCallbackProperties>(
            InteractionCallback.Modal(new ModalProperties("modal-it-ticket", "New IT Ticket")
            {
                new LabelProperties("Title", new TextInputProperties("title", TextInputStyle.Short)),
                new LabelProperties("Description",
                    new TextInputProperties("description", TextInputStyle.Paragraph) { Required = false }),
                new LabelProperties("Priority", new StringMenuProperties("priority",
                [
                    new StringMenuSelectOptionProperties("Auto (let the model decide)", "auto") { Default = true },
                    new StringMenuSelectOptionProperties("Urgent", "urgent"),
                    new StringMenuSelectOptionProperties("No rush", "no-rush"),
                ]) { Required = false }),
                new LabelProperties("Attachment", new FileUploadProperties("file") { Required = false, MaxValues = 1 }),
            }));
    }

    public static InteractionCallbackProperties<InteractionMessageProperties> HandleModalAsync(
        IWorldClient world, ModalInteractionContext c)
    {
        var fields = c.Components.OfType<Label>().Select(l => l.Component).ToList();
        string? Text(string id) => fields.OfType<TextInput>().FirstOrDefault(i => i.CustomId == id)?.Value;
        var fileUrl = fields.OfType<FileUpload>().FirstOrDefault(f => f.CustomId == "file")
            ?.Attachments.FirstOrDefault()?.Url;
        var priority = fields.OfType<StringMenu>().FirstOrDefault(m => m.CustomId == "priority")
            ?.SelectedValues?.FirstOrDefault() switch
        {
            "urgent" => TicketPriority.Urgent,
            "no-rush" => TicketPriority.NoRush,
            _ => TicketPriority.Auto,
        };
        _ = Task.Run(() => CreateAsync(world, c.User, c.Client.Rest, c.Interaction,
            Text("title"), Text("description"), priority, fileUrl, assigneeId: null));
        return InteractionCallback.DeferredMessage(MessageFlags.Ephemeral);
    }

    public static async Task CreateAsync(
        IWorldClient world, User requester, RestClient rest, Interaction interaction,
        string? title, string? description, TicketPriority priority, string? attachmentUrl, ulong? assigneeId)
    {
        var assignee = assigneeId ?? ItUserFromEnvironment();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ticket = await world.AskAsync<TicketCreate, TicketCreated>(new TicketCreate
        {
            Title = title,
            Description = description,
            RequestedPriority = priority,
            AttachmentUrl = attachmentUrl,
            Requester = requester.ToString(),
            Assignee = assignee is null ? null : $"<@{assignee}>",
        }, timeout.Token);

        var content = Card(ticket.View, "created", liveTimer: true);
        var buttons = FullRow(ticket.View.TicketId);
        var media = await DownloadAsync(ticket.View.AttachmentUrl).ConfigureAwait(false);

        if (interaction.GuildId is null)
        {
            // /it ran in a DM with the bot: the card stays in this conversation
            // as a normal message — survives a refresh, still only the user sees it.
            await rest.SendInteractionFollowupMessageAsync(interaction.ApplicationId, interaction.Token,
                new InteractionMessageProperties
                {
                    Content = content, Components = [buttons], Attachments = Attachments(media),
                });
        }
        else
        {
            try
            {
                var dm = await requester.GetDMChannelAsync();
                await rest.SendMessageAsync(dm.Id,
                    new MessageProperties
                    {
                        Content = content, Components = [buttons], Attachments = Attachments(media),
                    });
                await rest.SendInteractionFollowupMessageAsync(interaction.ApplicationId, interaction.Token,
                    new InteractionMessageProperties
                    {
                        Content = $"**IT ticket `{ticket.View.TicketId}` created** — sent to your DMs 📬",
                        Flags = MessageFlags.Ephemeral,
                    });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[it] DM to requester failed, card sent in channel instead: {ex.GetType().Name}: {ex.Message}");
                await rest.SendInteractionFollowupMessageAsync(interaction.ApplicationId, interaction.Token,
                    new InteractionMessageProperties
                    {
                        Content = content, Flags = MessageFlags.Ephemeral, Components = [buttons],
                        Attachments = Attachments(media),
                    });
            }
        }

        var recipients = new List<ulong>();
        if (assigneeId is { } chosen) recipients.Add(chosen);
        else recipients.AddRange(MentionedUsers(ticket.View.Assignee));
        if (recipients.Count == 0 && ItUserFromEnvironment() is { } fallback)
            recipients.Add(fallback);

        foreach (var staff in recipients.Where(id => id != requester.Id).Distinct())
        {
            try
            {
                var it = await rest.GetUserAsync(staff);
                var itDm = await it.GetDMChannelAsync();
                await rest.SendMessageAsync(itDm.Id, new MessageProperties
                {
                    Content = $"**Ticket `{ticket.View.TicketId}` assigned to you** — from {requester}\n" + content,
                    Components = [buttons],
                    Attachments = Attachments(media),
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[assign] notify failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    // Discord's CDN links for uploaded files are signed and expire; the file
    // re-attached by the bot is the bot's own attachment and outlives the link.
    private static async Task<(string Name, byte[] Bytes)?> DownloadAsync(string? url)
    {
        if (url is null) return null;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var bytes = await http.GetByteArrayAsync(url).ConfigureAwait(false);
            var name = url.Split('?')[0].Split('/').LastOrDefault() is { Length: > 0 } n ? n : "attachment";
            return (name, bytes);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[it] could not re-attach {url}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    // One fresh stream per message: a stream can only be uploaded once.
    private static List<AttachmentProperties>? Attachments((string Name, byte[] Bytes)? media) =>
        media is { } m ? [new AttachmentProperties(m.Name, new MemoryStream(m.Bytes))] : null;

    public static async Task<InteractionCallbackProperties> HandleStatusAsync(
        IWorldClient world, ButtonInteractionContext c, string status, string ticketId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var change = await world.AskAsync<TicketStatusChange, TicketChanged>(
            new TicketStatusChange { TicketId = ticketId, Status = status, ByUser = c.User.ToString() },
            timeout.Token);

        return InteractionCallback.ModifyMessage(m =>
        {
            m.Content = Card(change.View, $"status updated to `{status}`", liveTimer: false);
            m.Components = [FollowupRow(ticketId)];
        });
    }

    public static async Task<InteractionCallbackProperties> HandleReopenAsync(
        IWorldClient world, ButtonInteractionContext c, string ticketId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var change = await world.AskAsync<TicketStatusChange, TicketChanged>(
            new TicketStatusChange { TicketId = ticketId, Status = "reopened", ByUser = c.User.ToString() },
            timeout.Token);

        return InteractionCallback.ModifyMessage(m =>
        {
            m.Content = Card(change.View, "`reopened`", liveTimer: true);
            m.Components = [FullRow(ticketId)];
        });
    }

    public static async Task<InteractionCallbackProperties> HandleNoteAsync(
        IWorldClient world, ModalInteractionContext c, string ticketId)
    {
        var note = c.Components.OfType<Label>().Select(l => l.Component)
            .OfType<TextInput>().First(i => i.CustomId == "note").Value;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var added = await world.AskAsync<TicketNote, TicketNoteAdded>(
            new TicketNote { TicketId = ticketId, Note = note, ByUser = c.User.ToString() },
            timeout.Token);

        return InteractionCallback.ModifyMessage(m =>
        {
            m.Content = Card(added.View, $"note #{added.NoteCount} added", liveTimer: true) + $"\n> {added.Note}";
            m.Components = [FollowupRow(ticketId)];
        });
    }

    public static async Task<InteractionCallbackProperties> HandleReportAsync(
        IWorldClient world, ModalInteractionContext c, string ticketId)
    {
        var fields = c.Components.OfType<Label>().Select(l => l.Component).ToList();
        string Text(string id) => fields.OfType<TextInput>().FirstOrDefault(i => i.CustomId == id)?.Value ?? "";
        var fileUrl = fields.OfType<FileUpload>().FirstOrDefault(f => f.CustomId == "reportfile")
            ?.Attachments.FirstOrDefault()?.Url;
        var anonymous = fields.OfType<Checkbox>().FirstOrDefault(f => f.CustomId == "anonymous")?.Checked ?? true;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var filed = await world.AskAsync<TicketReport, TicketReported>(new TicketReport
        {
            TicketId = ticketId,
            Complaint = Text("complaint"),
            Action = Text("action"),
            FileUrl = fileUrl,
            Anonymous = anonymous,
            ByUser = c.User.ToString(),
        }, timeout.Token);

        return InteractionCallback.ModifyMessage(m =>
        {
            m.Content = Card(filed.View, "status updated to `complete`", liveTimer: false);
            m.Components = [FollowupRow(ticketId)];
        });
    }

    public static ActionRowProperties FullRow(string ticketId) => new()
    {
        new ButtonProperties($"itstatus:cancel:{ticketId}", "Cancel", ButtonStyle.Secondary),
        new ButtonProperties($"itstatus:complete:{ticketId}", "Complete", ButtonStyle.Success),
        new ButtonProperties($"itstatus:unsolved:{ticketId}", "Unsolved", ButtonStyle.Danger),
        new ButtonProperties($"itstatus:planned:{ticketId}", "Planned", ButtonStyle.Primary),
        new ButtonProperties($"itreport:{ticketId}", "Report", ButtonStyle.Secondary),
    };

    public static ActionRowProperties FollowupRow(string ticketId) => new()
    {
        new ButtonProperties($"itreopen:{ticketId}", "Reopen", ButtonStyle.Primary),
        new ButtonProperties($"itnote:{ticketId}", "Add note", ButtonStyle.Secondary),
        new ButtonProperties($"itreport:{ticketId}", "Report", ButtonStyle.Secondary),
    };

    public static string Card(in TicketView t, string status, bool liveTimer)
    {
        var header = $"**IT ticket `{t.TicketId}`** — {status}";
        if (t.Exists && t.CreatedAtUtc != default)
        {
            var age = DateTimeOffset.UtcNow - t.CreatedAtUtc;
            header += liveTimer
                ? $" — <t:{t.CreatedAtUtc.ToUnixTimeSeconds()}:R>"
                : $" (open for {FormatAge(age)})";
        }
        if (!t.Exists) return header;
        return header + "\n" + CardBody(t);
    }

    private static string CardBody(in TicketView t)
    {
        var body = $"Title: **{(string.IsNullOrWhiteSpace(t.Title) ? "(no title)" : t.Title)}**\n" +
            $"Priority: `{t.Priority}`" +
            (t.AutoClassified ? " *(auto-classified)*" : t.ClassifierOffline ? " *(classifier offline — defaulted)*" : "") +
            $"\n> {(string.IsNullOrWhiteSpace(t.Description) ? "(no description)" : t.Description)}";
        if (t.Assignee is not null) body += $"\n👤 Handled by {t.Assignee}";
        if (t.AttachmentUrl is not null) body += $"\n📎 {t.AttachmentUrl}";
        if (t.NoteCount > 0) body += $"\n📝 {t.NoteCount} note(s)";

        if (t.SolutionTitle is not null)
        {
            body += $"\n\n**💡 IT solution — {t.SolutionTitle}:**\n{t.SolutionText}";
            if (t.SolutionImage is not null) body += $"\n{t.SolutionImage}";
        }
        return body;
    }

    private static string FormatAge(TimeSpan a) => a.TotalHours >= 1
        ? $"{(int)a.TotalHours}h {a.Minutes}m"
        : a.TotalMinutes >= 1 ? $"{(int)a.TotalMinutes}m" : $"{(int)a.TotalSeconds}s";

    private static TicketPriority Map(PriorityOption p) => p switch
    {
        PriorityOption.Auto => TicketPriority.Auto,
        PriorityOption.NoRush => TicketPriority.NoRush,
        _ => TicketPriority.Urgent,
    };

    private static ulong? ItUserFromEnvironment() =>
        ulong.TryParse(Environment.GetEnvironmentVariable("Discord__ItUser"), out var u) ? u : null;

    [System.Text.RegularExpressions.GeneratedRegex(@"<@(\d+)>")]
    private static partial System.Text.RegularExpressions.Regex MentionRegex();

    private static IEnumerable<ulong> MentionedUsers(string? mentions)
    {
        if (mentions is null) yield break;
        foreach (System.Text.RegularExpressions.Match match in MentionRegex().Matches(mentions))
            yield return ulong.Parse(match.Groups[1].Value);
    }
}
