namespace CL.GameNetQuery.Models;

/// <summary>
/// A completed server query: what the server reported and how long it took. Returned as the
/// success value of a <c>Result&lt;ServerQuery&gt;</c> (see
/// <see cref="Valve.ValveUdpQuery.QueryAsync"/>); a query that fails is an <c>Error</c>, never a value of this type.
/// </summary>
/// <param name="Info">The server information.</param>
/// <param name="Players">The players on the server; empty when players were not requested.</param>
/// <param name="DurationMs">How long the whole query took, in milliseconds.</param>
public sealed record ServerQuery(ServerInfo Info, IReadOnlyList<PlayerInfo> Players, long DurationMs);
