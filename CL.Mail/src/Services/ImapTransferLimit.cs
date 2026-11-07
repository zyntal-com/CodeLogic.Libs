using MailKit;

namespace CL.Mail.Services;

/// <summary>Stops a server response that exceeds the explicitly requested wire budget.</summary>
internal sealed class ImapTransferLimit(long limit) : ITransferProgress
{
    public void Report(long bytesTransferred, long totalSize) => Report(bytesTransferred);
    public void Report(long bytesTransferred)
    {
        if (bytesTransferred > limit) throw new ImapBudgetException();
    }
}

internal sealed class ImapBudgetException : IOException;
