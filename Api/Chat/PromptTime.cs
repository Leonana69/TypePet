using System;
using System.Globalization;

namespace TypePet.Api.Chat;

/// <summary>
/// Formats "now" for injection into LLM system prompts. Both the in-app chatbot (<see cref="PetChatAgent"/>)
/// and declarative <c>kind:prompt</c> slash commands (<see cref="CommandInterpreter"/>) prepend the current
/// wall-clock time, so the model always knows the date/time without the command author adding a placeholder.
/// </summary>
public static class PromptTime
{
    /// <summary>The current local date and time as a model-friendly string, e.g.
    /// <c>"Sunday, June 7, 2026 at 3:45 PM (UTC-04:00)"</c>. The invariant culture keeps the English
    /// day/month names stable, and the UTC offset lets the model reason about the user's timezone.</summary>
    public static string Now() => Format(DateTime.Now);

    /// <summary>Pure formatter (takes the instant) so it's deterministic and unit-testable.</summary>
    public static string Format(DateTime now)
        => now.ToString("dddd, MMMM d, yyyy 'at' h:mm tt", CultureInfo.InvariantCulture)
           + " (UTC" + now.ToString("zzz", CultureInfo.InvariantCulture) + ")";
}
