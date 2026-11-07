using System.Collections.Concurrent;

namespace CL.SocialConnect.Services.Steam;

/// <summary>
/// Remembers the <c>openid.response_nonce</c> of every Steam sign-in assertion <see cref="SteamOpenIdService"/>
/// has verified, so the same assertion cannot be replayed. The default is <see cref="InMemoryOpenIdNonceStore"/>;
/// an application that runs on several servers must share one store between them (a database, a cache).
/// </summary>
public interface IOpenIdNonceStore
{
    /// <summary>
    /// Records <paramref name="nonce"/> unless it has been recorded before. The check and the record are one atomic
    /// step, so two concurrent verifications of the same assertion cannot both succeed.
    /// </summary>
    /// <param name="nonce">The assertion's <c>openid.response_nonce</c>.</param>
    /// <param name="expiresAtUtc">
    /// When the entry can be forgotten: after this moment <see cref="SteamOpenIdService.VerifyAsync(string, string, CancellationToken)"/>
    /// refuses the assertion by age anyway.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns><c>true</c> when the nonce was new and is now recorded; <c>false</c> when it was seen before.</returns>
    ValueTask<bool> TryUseAsync(string nonce, DateTime expiresAtUtc, CancellationToken cancellationToken = default);
}
