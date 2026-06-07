using System;
using System.Globalization;
using System.Threading;
using MaplePet.Engine;

namespace MaplePet.Api.Chat;

/// <summary>
/// Dev-only headless check for the <c>/remind</c> time parser and scheduler bookkeeping. The parser is pure
/// (takes a fixed "now"), so every case is deterministic; the scheduler check exercises schedule/list/cancel/
/// clear plus the self-removal-on-fire path (with a null pet, so nothing is spoken). No UI, no LLM. Wired to
/// <c>--remind-test</c>. Returns non-zero if any case fails.
/// </summary>
public static class RemindTest
{
    public static int Run()
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* redirected */ }

        // A fixed reference point: Sat 2026-06-06 13:00:00 local. Clock times after 13:00 land today; before,
        // they roll to tomorrow (2026-06-07).
        var now = new DateTime(2026, 6, 6, 13, 0, 0, DateTimeKind.Local);
        int fail = 0;

        Console.WriteLine($"now = {now:yyyy-MM-dd HH:mm:ss ddd}\n");
        Console.WriteLine("DURATIONS & CLOCK TIMES");

        // (input, expected Display, expected DueAt)
        (string, string, DateTime)[] ok =
        {
            ("10mins",    "in 10 minutes",            now.AddMinutes(10)),
            ("1hour",     "in 1 hour",                now.AddHours(1)),
            ("5s",        "in 5 seconds",             now.AddSeconds(5)),
            ("5sec",      "in 5 seconds",             now.AddSeconds(5)),
            ("90s",       "in 1 minute 30 seconds",   now.AddSeconds(90)),
            ("1.5h",      "in 1 hour 30 minutes",     now.AddMinutes(90)),
            ("1h30m",     "in 1 hour 30 minutes",     now.AddMinutes(90)),
            ("1h30min",   "in 1 hour 30 minutes",     now.AddMinutes(90)),
            ("2d",        "in 2 days",                now.AddDays(2)),
            ("30m",       "in 30 minutes",            now.AddMinutes(30)),
            ("5",         "in 5 minutes",             now.AddMinutes(5)),       // bare number = minutes
            ("14:23",     "at 2:23 PM",               new DateTime(2026, 6, 6, 14, 23, 0)),
            ("13:32:30",  "at 1:32:30 PM",            new DateTime(2026, 6, 6, 13, 32, 30)),
            ("12:30",     "at 12:30 PM tomorrow",     new DateTime(2026, 6, 7, 12, 30, 0)), // already past today
            ("1:30pm",    "at 1:30 PM",               new DateTime(2026, 6, 6, 13, 30, 0)),
            ("2pm",       "at 2:00 PM",               new DateTime(2026, 6, 6, 14, 0, 0)),
            ("9am",       "at 9:00 AM tomorrow",      new DateTime(2026, 6, 7, 9, 0, 0)),
            ("12am",      "at 12:00 AM tomorrow",     new DateTime(2026, 6, 7, 0, 0, 0)),  // midnight
            ("12pm",      "at 12:00 PM tomorrow",     new DateTime(2026, 6, 7, 12, 0, 0)), // noon, past today
        };

        foreach (var (input, wantDisplay, wantDue) in ok)
        {
            bool parsed = ReminderTimeParser.TryParse(input, now, out var r, out var err);
            bool good = parsed && r.Display == wantDisplay && r.DueAt == wantDue;
            if (!good) fail++;
            Console.WriteLine($"  {(good ? "PASS" : "FAIL")}  {input,-10} -> " +
                              (parsed ? $"{r.Display,-26} due {r.DueAt:yyyy-MM-dd HH:mm:ss}" : $"(rejected: {err})"));
            if (!good && parsed)
                Console.WriteLine($"        expected: {wantDisplay,-26} due {wantDue:yyyy-MM-dd HH:mm:ss}");
        }

        Console.WriteLine("\nREJECTED (these should NOT parse)");
        string[] bad = { "25:00", "14:99", "13:60:00", "13pm", "0pm", "abc", "10x", "0s", "0m", "", "  ", "1h30" };
        foreach (var input in bad)
        {
            bool parsed = ReminderTimeParser.TryParse(input, now, out _, out var err);
            if (parsed) fail++;
            Console.WriteLine($"  {(parsed ? "FAIL" : "PASS")}  \"{input}\"" + (parsed ? "" : $"  ({err})"));
        }

        Console.WriteLine("\nRECURRING (-d / -w / -m)");
        // (kind, body, expected time-of-day, expected first DueAt, expected message, expected weekday?, expected day?)
        fail += CheckRec(ReminderKind.Daily,   "14:23 do dailies", now, new TimeSpan(14, 23, 0), new DateTime(2026, 6, 6, 14, 23, 0), "do dailies", null, null);
        fail += CheckRec(ReminderKind.Daily,   "9:00 x",           now, new TimeSpan(9, 0, 0),   new DateTime(2026, 6, 7, 9, 0, 0),   "x", null, null);
        fail += CheckRec(ReminderKind.Daily,   "1:30pm x",         now, new TimeSpan(13, 30, 0),  new DateTime(2026, 6, 6, 13, 30, 0), "x", null, null);
        fail += CheckRec(ReminderKind.Daily,   "13:32:30 x",       now, new TimeSpan(13, 32, 30), new DateTime(2026, 6, 6, 13, 32, 30), "x", null, null);
        fail += CheckRec(ReminderKind.Daily,   "9:00",             now, new TimeSpan(9, 0, 0),    new DateTime(2026, 6, 7, 9, 0, 0),   "", null, null); // empty message OK at parser level
        fail += CheckRec(ReminderKind.Weekly,  "mon 20:00 raid",   now, new TimeSpan(20, 0, 0),   new DateTime(2026, 6, 8, 20, 0, 0),  "raid", DayOfWeek.Monday, null);
        fail += CheckRec(ReminderKind.Weekly,  "sat 20:00 x",      now, new TimeSpan(20, 0, 0),   new DateTime(2026, 6, 6, 20, 0, 0),  "x", DayOfWeek.Saturday, null);
        fail += CheckRec(ReminderKind.Weekly,  "sat 9:00 x",       now, new TimeSpan(9, 0, 0),    new DateTime(2026, 6, 13, 9, 0, 0),  "x", DayOfWeek.Saturday, null); // today's 9:00 passed → +7
        fail += CheckRec(ReminderKind.Weekly,  "20:00 raid",       now, new TimeSpan(20, 0, 0),   new DateTime(2026, 6, 6, 20, 0, 0),  "raid", DayOfWeek.Saturday, null); // weekday defaults to today
        fail += CheckRec(ReminderKind.Weekly,  "sunday 10:00 x",   now, new TimeSpan(10, 0, 0),   new DateTime(2026, 6, 7, 10, 0, 0),  "x", DayOfWeek.Sunday, null);
        fail += CheckRec(ReminderKind.Monthly, "1 09:00 pay rent", now, new TimeSpan(9, 0, 0),    new DateTime(2026, 7, 1, 9, 0, 0),   "pay rent", null, 1);
        fail += CheckRec(ReminderKind.Monthly, "09:00 pay rent",   now, new TimeSpan(9, 0, 0),    new DateTime(2026, 7, 6, 9, 0, 0),   "pay rent", null, 6); // day defaults to today (6)
        fail += CheckRec(ReminderKind.Monthly, "6 14:00 x",        now, new TimeSpan(14, 0, 0),   new DateTime(2026, 6, 6, 14, 0, 0),  "x", null, 6);
        fail += CheckRec(ReminderKind.Monthly, "31 09:00 x",       now, new TimeSpan(9, 0, 0),    new DateTime(2026, 6, 30, 9, 0, 0),  "x", null, 31); // June has 30 → clamp

        Console.WriteLine("\nRECURRING REJECTED");
        fail += RejectRec(ReminderKind.Daily,   "10m foo", now);       // duration, not a clock time
        fail += RejectRec(ReminderKind.Weekly,  "mon 1.5h foo", now);  // duration
        fail += RejectRec(ReminderKind.Weekly,  "funday 20:00 x", now); // unknown weekday
        fail += RejectRec(ReminderKind.Monthly, "0 09:00 x", now);     // day 0
        fail += RejectRec(ReminderKind.Monthly, "32 09:00 x", now);    // day 32
        fail += RejectRec(ReminderKind.Daily,   "", now);              // no clock time
        fail += RejectRec(ReminderKind.Weekly,  "mon", now);           // weekday but no clock
        fail += RejectRec(ReminderKind.Monthly, "5", now);             // day but no clock

        Console.WriteLine("\nNextOccurrence (recurrence math)");
        fail += CheckNext(Rec(ReminderKind.Daily, new TimeSpan(9, 0, 0)),  now, new DateTime(2026, 6, 7, 9, 0, 0),  "daily 09:00 → tomorrow");
        fail += CheckNext(Rec(ReminderKind.Daily, new TimeSpan(20, 0, 0)), now, new DateTime(2026, 6, 6, 20, 0, 0), "daily 20:00 → today");
        fail += CheckNext(RecW(new TimeSpan(8, 0, 0), DayOfWeek.Monday),   now, new DateTime(2026, 6, 8, 8, 0, 0),  "weekly Mon");
        fail += CheckNext(RecW(new TimeSpan(9, 0, 0), DayOfWeek.Saturday), now, new DateTime(2026, 6, 13, 9, 0, 0), "weekly Sat (passed) → +7");
        fail += CheckNext(RecM(new TimeSpan(9, 0, 0), 31), now, new DateTime(2026, 6, 30, 9, 0, 0), "monthly 31 in June → clamp 30");
        fail += CheckNext(RecM(new TimeSpan(9, 0, 0), 31), new DateTime(2026, 1, 31, 12, 0, 0), new DateTime(2026, 2, 28, 9, 0, 0), "monthly 31 → Feb 28 (non-leap)");
        fail += CheckNext(RecM(new TimeSpan(9, 0, 0), 29), new DateTime(2028, 1, 31, 12, 0, 0), new DateTime(2028, 2, 29, 9, 0, 0), "monthly 29 → Feb 29 (leap)");
        fail += CheckNext(RecM(new TimeSpan(10, 0, 0), 5), now, new DateTime(2026, 7, 5, 10, 0, 0), "monthly 5 (passed) → next month");

        Console.WriteLine("\nPERSISTENCE round-trip");
        fail += RoundTrip();

        Console.WriteLine("\nCHAT TOOL (set_reminder → /remind)");
        fail += ChatToolChecks();

        Console.WriteLine("\nSCHEDULER bookkeeping");
        fail += SchedulerChecks();

        Console.WriteLine(fail == 0 ? "\nOK: all reminder cases passed." : $"\nFAIL: {fail} reminder case(s) failed.");
        return fail == 0 ? 0 : 1;
    }

    private static int CheckRec(ReminderKind kind, string body, DateTime now, TimeSpan eTod, DateTime eDue,
        string eMsg, DayOfWeek? eWd, int? eDom)
    {
        bool ok = ReminderTimeParser.TryParseRecurring(kind, body, now, out var r, out var err);
        bool good = ok && r.Kind == kind && r.TimeOfDay == eTod && r.DueAt == eDue && r.Message == eMsg
                    && (eWd is null || r.Weekday == eWd) && (eDom is null || r.DayOfMonth == eDom);
        if (!good) { /* counted by caller */ }
        Console.WriteLine($"  {(good ? "PASS" : "FAIL")}  -{Flag(kind)} {body,-18} -> " +
            (ok ? $"{kind} tod={r.TimeOfDay} due {r.DueAt:yyyy-MM-dd HH:mm:ss} wd={r.Weekday} dom={r.DayOfMonth} msg=\"{r.Message}\""
                : $"(rejected: {err})"));
        if (!good && ok)
            Console.WriteLine($"        expected: {kind} tod={eTod} due {eDue:yyyy-MM-dd HH:mm:ss}" +
                (eWd is null ? "" : $" wd={eWd}") + (eDom is null ? "" : $" dom={eDom}") + $" msg=\"{eMsg}\"");
        return good ? 0 : 1;
    }

    private static int RejectRec(ReminderKind kind, string body, DateTime now)
    {
        bool ok = ReminderTimeParser.TryParseRecurring(kind, body, now, out _, out var err);
        Console.WriteLine($"  {(ok ? "FAIL" : "PASS")}  -{Flag(kind)} \"{body}\"" + (ok ? "" : $"  ({err})"));
        return ok ? 1 : 0;
    }

    private static int CheckNext(ReminderRecord r, DateTime after, DateTime expected, string label)
    {
        var got = r.NextOccurrence(after);
        bool good = got == expected;
        Console.WriteLine($"  {(good ? "PASS" : "FAIL")}  {label,-34} -> {got:yyyy-MM-dd HH:mm:ss}" +
            (good ? "" : $"  (expected {expected:yyyy-MM-dd HH:mm:ss})"));
        return good ? 0 : 1;
    }

    private static int RoundTrip()
    {
        var list = new System.Collections.Generic.List<ReminderRecord>
        {
            new() { Id = 1, Kind = ReminderKind.Once,    Message = "stretch", DueAt = new DateTime(2026, 6, 6, 14, 0, 0) },
            new() { Id = 2, Kind = ReminderKind.Daily,   Message = "dailies", TimeOfDay = new TimeSpan(14, 23, 0), DueAt = new DateTime(2026, 6, 6, 14, 23, 0) },
            new() { Id = 3, Kind = ReminderKind.Weekly,  Message = "raid",    TimeOfDay = new TimeSpan(20, 0, 0), Weekday = DayOfWeek.Monday, DueAt = new DateTime(2026, 6, 8, 20, 0, 0) },
            new() { Id = 4, Kind = ReminderKind.Monthly, Message = "rent",    TimeOfDay = new TimeSpan(9, 0, 0), DayOfMonth = 31, DueAt = new DateTime(2026, 6, 30, 9, 0, 0) },
        };
        var opts = new System.Text.Json.JsonSerializerOptions();
        opts.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        string json = System.Text.Json.JsonSerializer.Serialize(list, opts);
        var back = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.List<ReminderRecord>>(json, opts)
                   ?? new System.Collections.Generic.List<ReminderRecord>();

        int fail = back.Count == list.Count ? 0 : 1;
        for (int i = 0; i < Math.Min(back.Count, list.Count); i++)
        {
            var a = list[i]; var b = back[i];
            bool good = a.Id == b.Id && a.Kind == b.Kind && a.Message == b.Message && a.DueAt == b.DueAt
                        && a.TimeOfDay == b.TimeOfDay && a.Weekday == b.Weekday && a.DayOfMonth == b.DayOfMonth;
            if (!good) fail++;
            Console.WriteLine($"  {(good ? "PASS" : "FAIL")}  {a.Kind} #{a.Id} round-trips");
        }
        Console.WriteLine($"  json: {json}");
        return fail;
    }

    private static int ChatToolChecks()
    {
        int fail = 0;

        // 1) The model's structured args build the right /remind command (normalization + flags) — tested
        // with a fake runner that just captures the command string.
        string? captured = null;
        var fake = new ReminderChatTools((cmd, _) => { captured = cmd; return Task.FromResult(CommandResult.Ok("ok")); });
        (string args, string expected)[] cases =
        {
            ("""{"time":"10m","message":"do dailies"}""",                                          "/remind 10m do dailies"),
            ("""{"time":"in 10 minutes","message":"do dailies"}""",                                "/remind 10minutes do dailies"), // "in" + spaces stripped
            ("""{"time":"2:00 PM","message":"call mom"}""",                                        "/remind 2:00PM call mom"),
            ("""{"time":"9:00","message":"standup","repeat":"daily"}""",                           "/remind -d 9:00 standup"),
            ("""{"time":"20:00","message":"raid","repeat":"weekly","weekday":"monday"}""",         "/remind -w monday 20:00 raid"),
            ("""{"time":"20:00","message":"raid","repeat":"weekly"}""",                            "/remind -w 20:00 raid"),
            ("""{"time":"9:00","message":"pay rent","repeat":"monthly","day_of_month":1}""",       "/remind -m 1 9:00 pay rent"),
            ("""{"time":"9:00","message":"pay rent","repeat":"monthly"}""",                        "/remind -m 9:00 pay rent"),
        };
        foreach (var (args, expected) in cases)
        {
            captured = null;
            _ = fake.DispatchAsync(new ChatToolCall("t", "set_reminder", args), CancellationToken.None).GetAwaiter().GetResult();
            bool good = captured == expected;
            Console.WriteLine($"  {(good ? "PASS" : "FAIL")}  set_reminder {args}");
            Console.WriteLine($"        -> {captured}");
            if (!good) { fail++; Console.WriteLine($"        expected: {expected}"); }
        }

        captured = null;
        _ = fake.DispatchAsync(new ChatToolCall("t", "list_reminders", "{}"), CancellationToken.None).GetAwaiter().GetResult();
        fail += Expect(captured == "/remind list", "list_reminders → /remind list", captured);
        captured = null;
        _ = fake.DispatchAsync(new ChatToolCall("t", "cancel_reminder", """{"id":"all"}"""), CancellationToken.None).GetAwaiter().GetResult();
        fail += Expect(captured == "/remind cancel all", "cancel_reminder all → /remind cancel all", captured);

        // 2) End-to-end through the REAL ChatCommands (built-ins only, no pet/LLM/store): a chat-set reminder
        // actually lands in the shared scheduler and shows up in list_reminders.
        var commands = new ChatCommands(chatEnabled: () => false, buildConfig: () => null, pet: () => null);
        var tools = new ReminderChatTools((cmd, ct) => commands.RunAsync(cmd, ct));
        string setText = tools.DispatchAsync(
            new ChatToolCall("t", "set_reminder", """{"time":"9:00","message":"standup","repeat":"daily"}"""),
            CancellationToken.None).GetAwaiter().GetResult();
        fail += Expect(setText.Contains("every day", StringComparison.OrdinalIgnoreCase), "real set_reminder daily confirms", setText);
        string listText = tools.DispatchAsync(new ChatToolCall("t", "list_reminders", "{}"), CancellationToken.None).GetAwaiter().GetResult();
        fail += Expect(listText.Contains("daily 9:00", StringComparison.OrdinalIgnoreCase), "real list_reminders shows it", listText);

        return fail;
    }

    private static int Expect(bool ok, string label, string? got)
    {
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {label}" + (ok ? "" : $"   (got: {got})"));
        return ok ? 0 : 1;
    }

    private static ReminderRecord Rec(ReminderKind k, TimeSpan tod) => new() { Kind = k, TimeOfDay = tod };
    private static ReminderRecord RecW(TimeSpan tod, DayOfWeek wd) => new() { Kind = ReminderKind.Weekly, TimeOfDay = tod, Weekday = wd };
    private static ReminderRecord RecM(TimeSpan tod, int dom) => new() { Kind = ReminderKind.Monthly, TimeOfDay = tod, DayOfMonth = dom };
    private static string Flag(ReminderKind k) => k switch
    {
        ReminderKind.Daily => "d", ReminderKind.Weekly => "w", ReminderKind.Monthly => "m", _ => "?",
    };

    private static int SchedulerChecks()
    {
        int fail = 0;
        var sched = new ReminderScheduler(() => null); // null pet: fired reminders self-remove, speak nothing
        var soon = new DateTime(2030, 1, 1, 0, 0, 0);

        // DueAt is what List() sorts by, so vary it (consistent with each delay, as the real command does).
        int a = sched.Schedule(soon.AddHours(2), TimeSpan.FromHours(2), "two");
        int b = sched.Schedule(soon.AddHours(1), TimeSpan.FromHours(1), "one");
        int c = sched.Schedule(soon.AddHours(3), TimeSpan.FromHours(3), "three");

        var list = sched.List();
        bool listed = list.Count == 3 && list[0].Message == "one" && list[2].Message == "three"; // soonest first
        Console.WriteLine($"  {(listed ? "PASS" : "FAIL")}  list sorted soonest-first ({list.Count} entries)");
        if (!listed) fail++;

        bool cancelled = sched.Cancel(b) && !sched.Cancel(b) && sched.List().Count == 2;
        Console.WriteLine($"  {(cancelled ? "PASS" : "FAIL")}  cancel by id (and re-cancel is a no-op)");
        if (!cancelled) fail++;

        bool cleared = sched.Clear() == 2 && sched.List().Count == 0;
        Console.WriteLine($"  {(cleared ? "PASS" : "FAIL")}  clear removes the rest");
        if (!cleared) fail++;

        // Fire path: a sub-second reminder should self-remove from the list after it elapses.
        sched.Schedule(soon, TimeSpan.FromMilliseconds(50), "fired");
        Thread.Sleep(300);
        bool selfRemoved = sched.List().Count == 0;
        Console.WriteLine($"  {(selfRemoved ? "PASS" : "FAIL")}  fired reminder self-removes");
        if (!selfRemoved) fail++;
        sched.Dispose();

        // Recurring: ScheduleRecord arms a daily reminder (future), lists/snapshots it with its kind, cancels it.
        var sched2 = new ReminderScheduler(() => null);
        int rid = sched2.ScheduleRecord(new ReminderRecord { Kind = ReminderKind.Daily, Message = "dailies", TimeOfDay = new TimeSpan(9, 0, 0) });
        var rlist = sched2.List();
        var snap = sched2.Snapshot();
        bool rec = rlist.Count == 1 && rlist[0].Kind == ReminderKind.Daily && rlist[0].DueAt > DateTime.Now
                   && snap.Count == 1 && snap[0].Id == rid && sched2.Cancel(rid) && sched2.List().Count == 0;
        Console.WriteLine($"  {(rec ? "PASS" : "FAIL")}  recurring schedule/list/snapshot/cancel");
        if (!rec) fail++;
        sched2.Dispose();

        _ = a; _ = c; _ = CultureInfo.InvariantCulture;
        return fail;
    }
}
