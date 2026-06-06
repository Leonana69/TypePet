using System;

namespace MaplePet.Api.Chat;

/// <summary>Raised when a <c>/rank</c> lookup fails. <see cref="Exception.Message"/> is user-facing (shown
/// in the pet's bubble), so keep it short and plain. Thrown by every rank source — the GMS rankings
/// (<see cref="NexonGmsRankApi"/>), the maple.gg scrape (<see cref="MapleGgScraper"/>), and the maple-kit
/// proxy (<see cref="MapleKitApi"/>).</summary>
public sealed class RankException : Exception
{
    public RankException(string message) : base(message) { }
}
