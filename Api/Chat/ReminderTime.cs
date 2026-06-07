using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using MaplePet.Engine;

namespace MaplePet.Api.Chat;

/// <summary>The parsed result of a <c>/remind</c> time spec: <see cref="DueAt"/> is the absolute wall-clock
/// moment the reminder should fire, <see cref="Delay"/> is the wait from "now" (what the scheduler arms its
/// timer with), <see cref="Display"/> is a human phrase for the confirmation ("in 10 minutes" / "at 2:23 PM
/// tomorrow"), and <see cref="IsAbsolute"/> distinguishes a clock time from a duration.</summary>
public readonly record struct ReminderTime(DateTime DueAt, TimeSpan Delay, string Display, bool IsAbsolute);

/// <summary>
/// Parses the single leading <i>time token</i> of a <c>/remind</c> command into a concrete due time. The
/// time is always exactly the first whitespace-delimited token, so the rest of the line is free-form message
/// text — no ambiguity over where the time ends. Two shapes are understood:
///
/// <list type="bullet">
/// <item><b>Durations</b> — <c>5s</c>, <c>10mins</c>, <c>1hour</c>, <c>90s</c>, <c>1.5h</c>, <c>1h30m</c>,
/// <c>2d</c>. A number followed by a unit (s/sec/secs/second(s), m/min(s)/minute(s), h/hr(s)/hour(s),
/// d/day(s)); compound forms with no spaces chain (<c>1h30m</c>); a bare number defaults to minutes.</item>
/// <item><b>Absolute clock times</b> — <c>14:23</c>, <c>13:32:30</c>, <c>1:30pm</c>, <c>2pm</c>. 24-hour by
/// default; an <c>am</c>/<c>pm</c> suffix switches to 12-hour. The next occurrence is chosen: a time already
/// past today rolls to tomorrow.</item>
/// </list>
///
/// A token is treated as an absolute clock time when it contains a <c>:</c> or ends with <c>am</c>/<c>pm</c>;
/// otherwise it's a duration (so a bare <c>14</c> is 14 minutes, not 14:00). The parser is pure and takes
/// <c>now</c> as an argument so it's deterministic and unit-testable (see <see cref="RemindTest"/>).
/// </summary>
public static class ReminderTimeParser
{
    /// <summary>The longest duration a reminder may be set for (clock times are always &lt; 24h out, so this
    /// only bounds durations). Keeps the underlying timer well within range and rejects typo-grade values.</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromDays(30);

    /// <summary>Parse the leading time token into a <see cref="ReminderTime"/>. Never throws: a bad token
    /// returns false with a short, user-facing <paramref name="error"/> phrase.</summary>
    public static bool TryParse(string token, DateTime now, out ReminderTime result, out string? error)
    {
        result = default;
        error = null;
        string t = (token ?? "").Trim();
        if (t.Length == 0) { error = "no time given"; return false; }

        // A colon or an am/pm suffix marks a clock time; everything else is a duration (so a bare "14" is
        // 14 minutes, not 14:00 — durations are the common case and stay unsurprising).
        bool looksAbsolute = t.Contains(':') || Regex.IsMatch(t, @"[ap]m$", RegexOptions.IgnoreCase);
        if (looksAbsolute) return TryParseAbsolute(t, now, out result, out error);

        if (!TryParseDuration(t, out var span, out error)) return false;
        if (span <= TimeSpan.Zero) { error = "the duration must be greater than zero"; return false; }
        if (span > MaxDelay) { error = $"that's too far off — keep it under {MaxDelay.TotalDays:0} days"; return false; }

        result = new ReminderTime(now + span, span, "in " + Humanize(span), false);
        return true;
    }

    /// <summary>Parse an absolute clock time (<c>HH:MM[:SS]</c> 24-hour, or <c>H[:MM[:SS]]am/pm</c> 12-hour)
    /// and resolve it to the next occurrence relative to <paramref name="now"/>.</summary>
    private static bool TryParseAbsolute(string t, DateTime now, out ReminderTime result, out string? error)
    {
        result = default;
        error = null;

        var m = Regex.Match(t, @"^(\d{1,2})(?::(\d{1,2}))?(?::(\d{1,2}))?\s*([ap]m)?$", RegexOptions.IgnoreCase);
        if (!m.Success) { error = $"\"{t}\" isn't a clock time I understand"; return false; }

        int hh = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        int mm = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
        int ss = m.Groups[3].Success ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
        bool hasSeconds = m.Groups[3].Success;
        string ap = m.Groups[4].Value.ToLowerInvariant();

        if (ap.Length == 2) // 12-hour with am/pm
        {
            if (hh is < 1 or > 12) { error = $"{hh} isn't a valid 12-hour hour (1–12)"; return false; }
            if (ap == "pm" && hh != 12) hh += 12;
            else if (ap == "am" && hh == 12) hh = 0;
        }
        else if (hh > 23) { error = $"{hh} isn't a valid hour (0–23)"; return false; }

        if (mm > 59) { error = "minutes must be 0–59"; return false; }
        if (ss > 59) { error = "seconds must be 0–59"; return false; }

        var todayDue = new DateTime(now.Year, now.Month, now.Day, hh, mm, ss, now.Kind);
        var due = todayDue <= now ? todayDue.AddDays(1) : todayDue; // already passed today → tomorrow
        bool tomorrow = due.Date != now.Date;

        string clock = (hasSeconds ? due.ToString("h:mm:ss tt", CultureInfo.InvariantCulture)
                                   : due.ToString("h:mm tt", CultureInfo.InvariantCulture));
        result = new ReminderTime(due, due - now, "at " + clock + (tomorrow ? " tomorrow" : ""), true);
        return true;
    }

    /// <summary>Parse a duration token (a chain of number+unit pairs, or a bare number = minutes) into a
    /// <see cref="TimeSpan"/>. Lenient about internal whitespace; strict about unknown/missing units.</summary>
    private static bool TryParseDuration(string token, out TimeSpan span, out string? error)
    {
        span = TimeSpan.Zero;
        error = null;
        string t = token.Trim().ToLowerInvariant();

        double totalSeconds = 0;
        bool any = false;
        int i = 0;
        while (i < t.Length)
        {
            if (char.IsWhiteSpace(t[i])) { i++; continue; }

            int numStart = i;
            while (i < t.Length && (char.IsDigit(t[i]) || t[i] == '.')) i++;
            if (i == numStart) { error = $"\"{token}\" isn't a duration I understand"; return false; }
            if (!double.TryParse(t[numStart..i], NumberStyles.Float, CultureInfo.InvariantCulture, out double num))
            { error = $"\"{t[numStart..i]}\" isn't a number"; return false; }

            int unitStart = i;
            while (i < t.Length && char.IsLetter(t[i])) i++;
            string unit = t[unitStart..i];

            double unitSeconds;
            if (unit.Length == 0)
            {
                // A bare number is only meaningful as the whole token (default = minutes); a number with no
                // unit mid-chain (e.g. "1h30") is ambiguous, so reject it and ask for "1h30m".
                if (numStart == 0 && i == t.Length) unitSeconds = 60;
                else { error = "add a unit, e.g. 1h30m"; return false; }
            }
            else
            {
                unitSeconds = UnitSeconds(unit);
                if (unitSeconds < 0) { error = $"\"{unit}\" isn't a time unit (use s, m, h, or d)"; return false; }
            }

            totalSeconds += num * unitSeconds;
            any = true;
        }

        if (!any) { error = $"\"{token}\" isn't a duration I understand"; return false; }
        span = TimeSpan.FromSeconds(totalSeconds);
        return true;
    }

    /// <summary>Seconds per unit of the given (already lowercased) unit word, or -1 if unrecognised.</summary>
    private static double UnitSeconds(string unit) => unit switch
    {
        "s" or "sec" or "secs" or "second" or "seconds" => 1,
        "m" or "min" or "mins" or "minute" or "minutes" => 60,
        "h" or "hr" or "hrs" or "hour" or "hours" => 3600,
        "d" or "day" or "days" => 86400,
        _ => -1,
    };

    /// <summary>Render a positive <see cref="TimeSpan"/> as a friendly phrase ("10 minutes", "1 hour 30
    /// minutes", "2 days"), showing at most the two largest non-zero units. Used for confirmations and the
    /// <c>/remind list</c> "due in …" column.</summary>
    public static string Humanize(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        var parts = new List<string>(4);
        int days = (int)span.TotalDays;
        if (days > 0) parts.Add(Plural(days, "day"));
        if (span.Hours > 0) parts.Add(Plural(span.Hours, "hour"));
        if (span.Minutes > 0) parts.Add(Plural(span.Minutes, "minute"));
        if (span.Seconds > 0) parts.Add(Plural(span.Seconds, "second"));
        return parts.Count == 0 ? "now" : string.Join(" ", parts.Take(2));
    }

    private static string Plural(int n, string unit) => $"{n} {unit}{(n == 1 ? "" : "s")}";

    // ---- recurring reminders (-d / -w / -m) --------------------------------------------------------

    /// <summary>Parse the body of a recurring <c>/remind</c> (everything after the <c>-d</c>/<c>-w</c>/<c>-m</c>
    /// flag) into a <see cref="ReminderRecord"/> with its first <see cref="ReminderRecord.DueAt"/> resolved.
    /// The clock time is reused from <see cref="TryParse"/> and MUST be absolute (a duration is rejected).
    /// For weekly, an optional leading weekday is consumed (else it defaults to the first occurrence's
    /// weekday); for monthly, an optional leading day-of-month integer (else today's day). The remaining
    /// text becomes <see cref="ReminderRecord.Message"/> (may be empty — the caller reports a missing
    /// message). Pure: takes <paramref name="now"/>, so it's deterministic and unit-testable.</summary>
    public static bool TryParseRecurring(ReminderKind kind, string body, DateTime now,
        out ReminderRecord record, out string? error)
    {
        record = new ReminderRecord { Kind = kind };
        error = null;
        string s = (body ?? "").Trim();

        DayOfWeek? weekday = null;
        int? dayOfMonth = null;

        if (kind == ReminderKind.Weekly)
        {
            var (tok, rest) = SplitToken(s);
            // A clock token (it has ':' or am/pm) means no weekday was given → default it. Any other
            // non-clock word must be a weekday; an unrecognised one is an error.
            if (tok.Length > 0 && !LooksLikeClock(tok))
            {
                if (!TryParseWeekday(tok, out var wd))
                {
                    error = $"I don't know the weekday \"{tok}\". Use mon/monday … sun/sunday.";
                    return false;
                }
                weekday = wd;
                s = rest;
            }
        }
        else if (kind == ReminderKind.Monthly)
        {
            var (tok, rest) = SplitToken(s);
            // A non-clock leading token here is the day-of-month; it must be an integer 1–31.
            if (tok.Length > 0 && !LooksLikeClock(tok))
            {
                if (!IsAllDigits(tok) || !int.TryParse(tok, NumberStyles.None, CultureInfo.InvariantCulture, out int dom)
                    || dom < 1 || dom > 31)
                {
                    error = $"\"{tok}\" isn't a day of the month (use 1–31).";
                    return false;
                }
                dayOfMonth = dom;
                s = rest;
            }
        }

        var (clockTok, msgRest) = SplitToken(s);
        if (clockTok.Length == 0)
        {
            error = $"{KindWord(kind)} needs a clock time like 9:00 or 2:30pm.";
            return false;
        }
        if (!TryParse(clockTok, now, out var rt, out var clockErr))
        {
            error = $"I couldn't read the time \"{clockTok}\" — {clockErr}.";
            return false;
        }
        if (!rt.IsAbsolute)
        {
            error = $"{KindWord(kind)} needs a clock time, not a duration — try /remind {FlagOf(kind)} 9:00 foo. " +
                    $"(A duration like \"{clockTok}\" is a one-off: /remind {clockTok} foo.)";
            return false;
        }

        // rt.DueAt is the next occurrence of this clock time, so its time-of-day is the clock and (for a
        // defaulted weekly weekday) its weekday is the natural first occurrence.
        record.TimeOfDay = rt.DueAt.TimeOfDay;
        record.Message = msgRest;
        if (kind == ReminderKind.Weekly) record.Weekday = weekday ?? rt.DueAt.DayOfWeek;
        if (kind == ReminderKind.Monthly) record.DayOfMonth = dayOfMonth ?? now.Day;
        record.DueAt = record.NextOccurrence(now);
        return true;
    }

    /// <summary>Map a weekday word (3-letter or full, case-insensitive) to a <see cref="DayOfWeek"/>.</summary>
    public static bool TryParseWeekday(string token, out DayOfWeek weekday)
    {
        weekday = DayOfWeek.Monday;
        switch ((token ?? "").Trim().ToLowerInvariant())
        {
            case "mon": case "monday": weekday = DayOfWeek.Monday; return true;
            case "tue": case "tues": case "tuesday": weekday = DayOfWeek.Tuesday; return true;
            case "wed": case "weds": case "wednesday": weekday = DayOfWeek.Wednesday; return true;
            case "thu": case "thur": case "thurs": case "thursday": weekday = DayOfWeek.Thursday; return true;
            case "fri": case "friday": weekday = DayOfWeek.Friday; return true;
            case "sat": case "saturday": weekday = DayOfWeek.Saturday; return true;
            case "sun": case "sunday": weekday = DayOfWeek.Sunday; return true;
            default: return false;
        }
    }

    /// <summary>Split the first whitespace-delimited token off <paramref name="s"/>, returning it plus the
    /// trimmed remainder ("" when there's none).</summary>
    private static (string first, string rest) SplitToken(string s)
    {
        s = s.TrimStart();
        int sp = s.IndexOfAny(new[] { ' ', '\t', '\n', '\r' });
        return sp < 0 ? (s, "") : (s[..sp], s[(sp + 1)..].Trim());
    }

    private static bool LooksLikeClock(string tok) =>
        tok.Contains(':') || Regex.IsMatch(tok, @"[ap]m$", RegexOptions.IgnoreCase);

    private static bool IsAllDigits(string s)
    {
        if (s.Length == 0) return false;
        foreach (char c in s) if (!char.IsDigit(c)) return false;
        return true;
    }

    private static string KindWord(ReminderKind k) => k switch
    {
        ReminderKind.Daily => "-d (daily)",
        ReminderKind.Weekly => "-w (weekly)",
        ReminderKind.Monthly => "-m (monthly)",
        _ => "a recurring reminder",
    };

    private static string FlagOf(ReminderKind k) => k switch
    {
        ReminderKind.Daily => "-d",
        ReminderKind.Weekly => "-w",
        ReminderKind.Monthly => "-m",
        _ => "-d",
    };
}
