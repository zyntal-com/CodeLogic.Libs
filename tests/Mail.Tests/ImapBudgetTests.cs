using CL.Mail.Models;
using CL.Mail.Services;
using MailKit;
using MimeKit;
using Xunit;

namespace Mail.Tests;

public sealed class ImapBudgetTests
{
    private static BodyPartBasic Attachment(string id, uint size = 4) => new(new ContentType("application", "octet-stream"), id)
    {
        ContentDisposition = new ContentDisposition("attachment"), ContentTransferEncoding = "binary", Octets = size
    };

    [Fact]
    public async Task Message_attachment_budget_is_shared_across_parts()
    {
        var body = new BodyPartMultipart(new ContentType("multipart", "mixed"), "");
        body.BodyParts.Add(Attachment("1")); body.BodyParts.Add(Attachment("2"));
        var summary = new MessageSummary(0) { UniqueId = new UniqueId(1), Body = body, Envelope = new Envelope() };
        var message = await ImapMessageReader.ReadAsync(summary, "INBOX",
            new ImapConfig { MaxMessageBytes = 5, MaxAttachmentBytes = 4 }, false, true,
            (_, _, _) => Task.FromResult<Stream>(new MemoryStream(new byte[4])), default);
        Assert.Equal(4, message.Attachments.Sum(a => a.Content?.Length ?? 0));
        Assert.True(message.Attachments[1].TooLarge);
        Assert.True(message.ContentTruncated);
    }

    [Fact]
    public async Task Metadata_only_fetch_never_opens_attachment_sections()
    {
        var body = new BodyPartMultipart(new ContentType("multipart", "mixed"), "");
        body.BodyParts.Add(Attachment("1", uint.MaxValue));
        var summary = new MessageSummary(0) { UniqueId = new UniqueId(1), Body = body, Envelope = new Envelope() };
        var message = await ImapMessageReader.ReadAsync(summary, "INBOX", new ImapConfig(), true, false,
            (_, _, _) => throw new InvalidOperationException("Attachment was fetched eagerly"), default);
        Assert.Null(Assert.Single(message.Attachments).Content);
        Assert.Equal("1", message.Attachments[0].PartSpecifier);
    }

    [Fact]
    public async Task Actual_bytes_override_a_forged_small_declared_part_size()
    {
        var item = await ImapMessageReader.ReadPartAsync(Attachment("1", 1), 4,
            (_, _, _) => Task.FromResult<Stream>(new MemoryStream(new byte[5000])), default);
        Assert.True(item.TooLarge);
        Assert.Null(item.Content);
    }

    [Fact]
    public async Task Bodies_and_attachments_share_the_message_budget()
    {
        var body = new BodyPartMultipart(new ContentType("multipart", "mixed"), "");
        body.BodyParts.Add(new BodyPartText(new ContentType("text", "plain"), "1") { ContentTransferEncoding = "binary", Octets = 4 });
        body.BodyParts.Add(Attachment("2"));
        var summary = new MessageSummary(0) { UniqueId = new UniqueId(1), Body = body, Envelope = new Envelope(), Flags = MailKit.MessageFlags.Answered };
        var message = await ImapMessageReader.ReadAsync(summary, "INBOX", new ImapConfig { MaxMessageBytes = 5 }, true, true,
            (_, _, _) => Task.FromResult<Stream>(new MemoryStream("abcd"u8.ToArray())), default);
        Assert.Equal("abcd", message.TextBody);
        Assert.Null(Assert.Single(message.Attachments).Content);
        Assert.Equal(CL.Mail.Models.MessageFlags.Answered, message.Flags);
    }
}
