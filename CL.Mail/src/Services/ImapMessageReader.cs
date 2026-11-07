using CL.Mail.Models;
using MailKit;
using MimeKit;

namespace CL.Mail.Services;

/// <summary>Fetches individual bounded MIME sections; never downloads the complete message.</summary>
internal static class ImapMessageReader
{
    internal static async Task<ReceivedMessage> ReadAsync(IMessageSummary summary, string folder, ImapConfig config,
        bool includeBody, bool includeAttachments, Func<BodyPartBasic, int, CancellationToken, Task<Stream>> open, CancellationToken ct)
    {
        var remaining = (long)Math.Clamp(config.MaxMessageBytes, 1, 134_217_728);
        var truncated = false;
        string? text = null, html = null;
        if (includeBody)
        {
            text = await BodyAsync(summary.TextBody).ConfigureAwait(false);
            html = await BodyAsync(summary.HtmlBody).ConfigureAwait(false);
        }
        var attachments = new List<ReceivedAttachment>();
        foreach (var part in summary.Attachments)
        {
            if (attachments.Count >= Math.Clamp(config.MaxAttachments, 1, 1000)) { truncated = true; break; }
            var item = Stub(part);
            if (includeAttachments)
            {
                item = await ReadPartAsync(part, Math.Min(Math.Max(1, config.MaxAttachmentBytes), remaining), open, ct).ConfigureAwait(false);
                if (item.Content is { } bytes) remaining -= bytes.Length;
                truncated |= item.TooLarge;
            }
            attachments.Add(item);
        }
        var env = summary.Envelope ?? throw new InvalidDataException("The message has no envelope.");
        return new ReceivedMessage
        {
            MessageId = env.MessageId, Uid = summary.UniqueId.Id,
            From = env.From?.Mailboxes.FirstOrDefault()?.Address, FromName = env.From?.Mailboxes.FirstOrDefault()?.Name,
            To = env.To?.Mailboxes.Select(m => m.Address).ToList() ?? [], Cc = env.Cc?.Mailboxes.Select(m => m.Address).ToList() ?? [],
            Subject = env.Subject, Date = env.Date ?? DateTimeOffset.MinValue,
            Flags = ImapService.ConvertFlags(summary.Flags ?? MailKit.MessageFlags.None),
            Folder = folder, TextBody = text, HtmlBody = html, Attachments = attachments, ContentTruncated = truncated
        };

        async Task<string?> BodyAsync(BodyPartText? body)
        {
            if (body is null) return null;
            var value = await ReadPartAsync(body, Math.Min(Math.Clamp(config.MaxBodyBytes, 1, 16_777_216), remaining), open, ct).ConfigureAwait(false);
            if (value.Content is not { } bytes) { truncated = true; return null; }
            remaining -= bytes.Length;
            System.Text.Encoding encoding;
            try { encoding = System.Text.Encoding.GetEncoding(body.ContentType.Charset ?? "utf-8"); }
            catch (ArgumentException) { encoding = System.Text.Encoding.UTF8; }
            return encoding.GetString(bytes);
        }
    }

    internal static ReceivedAttachment Stub(BodyPartBasic part) => new()
    {
        FileName = part.FileName, PartSpecifier = part.PartSpecifier, ContentType = part.ContentType.MimeType, EncodedSize = part.Octets
    };

    internal static async Task<ReceivedAttachment> ReadPartAsync(BodyPartBasic part, long budget,
        Func<BodyPartBasic, int, CancellationToken, Task<Stream>> open, CancellationToken ct)
    {
        var stub = Stub(part);
        if (budget <= 0) return stub with { TooLarge = true };
        budget = Math.Min(budget, 134_217_728);
        var wireLimit = (int)Math.Min(int.MaxValue, budget * 4 + 4096);
        // This is only an early refusal: bytes read and decoded are independently limited below.
        if (part.Octets > wireLimit) return stub with { TooLarge = true };
        try
        {
            var stream = await open(part, wireLimit, ct).ConfigureAwait(false);
            await using var streamLifetime = stream.ConfigureAwait(false);
            using var bounded = new ImapBoundedReadStream(stream, wireLimit);
            using var fallback = stream.CanSeek ? null : new MemoryStream();
            if (fallback is not null)
            {
                await bounded.CopyToAsync(fallback, ct).ConfigureAwait(false);
                fallback.Position = 0;
            }
            using var mime = new MimePart(part.ContentType) { Content = new MimeContent(fallback ?? (Stream)bounded, Encoding(part.ContentTransferEncoding)) };
            var decoded = await ImapService.ReadAttachmentAsync(mime, budget, ct).ConfigureAwait(false);
            return stub with { Size = decoded.Size, Content = decoded.Content, TooLarge = decoded.TooLarge };
        }
        catch (ImapBudgetException) { return stub with { TooLarge = true }; }
    }

    private static ContentEncoding Encoding(string? name) => name?.ToLowerInvariant() switch
    {
        "base64" => ContentEncoding.Base64, "quoted-printable" => ContentEncoding.QuotedPrintable,
        "7bit" => ContentEncoding.SevenBit, "8bit" => ContentEncoding.EightBit, "binary" => ContentEncoding.Binary,
        "x-uuencode" or "uuencode" => ContentEncoding.UUEncode, _ => ContentEncoding.Default
    };
}
