using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using TypePet.Api;
using TypePet.Engine;

namespace TypePet.Api.Chat;

/// <summary>
/// Holds pending <c>/remind</c> reminders and speaks them through the pet when their time arrives. Each
/// reminder is backed by a <see cref="Timer"/>; when it fires the message is delivered via
/// <see cref="IPetControl.Say"/> (which marshals onto the UI thread itself), so the pet announces the
/// reminder even when the say bar is closed. A one-off reminder is dropped after it fires; a recurring one
/// (daily/weekly/monthly — see <see cref="ReminderRecord"/>) recomputes its next occurrence and re-arms.
///
/// Lives for the app's lifetime (owned by <see cref="ChatCommands"/>), so reminders survive command
/// hot-reloads. Every mutation (schedule, cancel, clear, and a fire that re-arms or completes) invokes the
/// <c>onChanged</c> callback so the owner can persist the list to <c>settings.json</c> — reminders survive
/// an app restart. Thread-safe: scheduling, firing, listing and cancelling all coordinate under one lock;
/// <see cref="IPetControl.Say"/> and <c>onChanged</c> are invoked outside it.
/// </summary>
public sealed class ReminderScheduler : IDisposable
{
    private sealed class Entry
    {
        public ReminderRecord Record = null!;
        public Timer? Timer;
    }

    // System.Threading.Timer's long overload tops out at ~49.7 days. The longest recurrence interval is
    // monthly (~31 days) and the duration parser caps one-offs at 30 days, so a single timer always reaches
    // the true due time; the clamp is just a hard safety bound against an overflowing arm value.
    private const long MaxTimerMs = 0xFFFFFFFEL;

    private readonly Func<IPetControl?> _pet;
    private readonly Action? _onChanged;
    private readonly object _gate = new();
    private readonly List<Entry> _entries = new();
    private int _nextId = 1;
    private bool _disposed;

    /// <param name="pet">Resolves the live pet at fire time (lazily, so the scheduler can arm reminders
    /// before the pet is ready).</param>
    /// <param name="onChanged">Invoked after every change so the owner can persist the reminder list.
    /// Never invoked from <see cref="Dispose"/> (shutdown must not rewrite the saved list).</param>
    public ReminderScheduler(Func<IPetControl?> pet, Action? onChanged = null)
    {
        _pet = pet;
        _onChanged = onChanged;
    }

    /// <summary>Schedule a one-off <paramref name="message"/> to be spoken at <paramref name="dueAt"/>,
    /// after waiting <paramref name="delay"/> from now. Returns the new reminder's id. A non-positive delay
    /// fires (essentially) immediately. (Recurring reminders use <see cref="ScheduleRecord"/>.)</summary>
    public int Schedule(DateTime dueAt, TimeSpan delay, string message)
    {
        long ms = ArmMs(delay);
        var rec = new ReminderRecord { Kind = ReminderKind.Once, DueAt = dueAt, Message = message ?? "" };
        int id;
        lock (_gate)
        {
            if (_disposed) return 0;
            id = AddArmedLocked(rec, ms);
        }
        _onChanged?.Invoke();
        return id;
    }

    /// <summary>Schedule a reminder from a <see cref="ReminderRecord"/> (one-off or recurring). Computes the
    /// next fire from now, caches it on the record, and arms. Returns the reminder's id (kept if the record
    /// already carries one — used by <see cref="Load"/> — else assigned).</summary>
    public int ScheduleRecord(ReminderRecord rec)
    {
        if (rec is null) return 0;
        rec.Sanitize();
        var now = DateTime.Now;
        rec.DueAt = rec.NextOccurrence(now);
        long ms = ArmMs(rec.DueAt - now);
        int id;
        lock (_gate)
        {
            if (_disposed) return 0;
            id = AddArmedLocked(rec, ms);
        }
        _onChanged?.Invoke();
        return id;
    }

    /// <summary>Rehydrate persisted reminders at startup. Recurring reminders recompute their next fire from
    /// now; a one-off whose time already passed while the app was closed is dropped. Ids are preserved (and
    /// the counter continued past the highest). Triggers one <c>onChanged</c> so dropped/recomputed records
    /// are written back.</summary>
    public void Load(IEnumerable<ReminderRecord> records)
    {
        if (records is null) return;
        var now = DateTime.Now;
        var armed = false;
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var rec in records)
            {
                if (rec is null) continue;
                rec.Sanitize();
                DateTime next;
                if (rec.Kind == ReminderKind.Once)
                {
                    if (rec.DueAt <= now) continue; // missed while the app was closed → drop
                    next = rec.DueAt;
                }
                else next = rec.NextOccurrence(now);
                rec.DueAt = next;
                AddArmedLocked(rec, ArmMs(next - now));
                armed = true;
            }
        }
        if (armed || records.Any()) _onChanged?.Invoke(); // persist back (drops + recomputed due times)
    }

    /// <summary>A copy of every pending reminder (for persistence). Safe to serialize off-thread.</summary>
    public IReadOnlyList<ReminderRecord> Snapshot()
    {
        lock (_gate) return _entries.Select(e => Copy(e.Record)).ToArray();
    }

    /// <summary>The pending reminders (copies), soonest first.</summary>
    public IReadOnlyList<ReminderRecord> List()
    {
        lock (_gate)
            return _entries.OrderBy(e => e.Record.DueAt).Select(e => Copy(e.Record)).ToArray();
    }

    /// <summary>Cancel the reminder with the given id. Returns false if no such reminder is pending.</summary>
    public bool Cancel(int id)
    {
        Entry? found;
        lock (_gate)
        {
            found = _entries.FirstOrDefault(e => e.Record.Id == id);
            if (found is null) return false;
            _entries.Remove(found);
        }
        found.Timer?.Dispose();
        _onChanged?.Invoke();
        return true;
    }

    /// <summary>Cancel every pending reminder; returns how many were cleared.</summary>
    public int Clear()
    {
        Entry[] all;
        lock (_gate)
        {
            all = _entries.ToArray();
            _entries.Clear();
        }
        foreach (var e in all) e.Timer?.Dispose();
        if (all.Length > 0) _onChanged?.Invoke();
        return all.Length;
    }

    public void Dispose()
    {
        // Tear down timers WITHOUT firing onChanged — disposing at shutdown must not rewrite (wipe) the
        // persisted reminder list.
        Entry[] all;
        lock (_gate)
        {
            _disposed = true;
            all = _entries.ToArray();
            _entries.Clear();
        }
        foreach (var e in all) e.Timer?.Dispose();
    }

    // Create a disarmed timer, add the entry, then arm it — all under _gate — so a very short delay can't
    // fire (and try to mutate the list) before the entry has been added. Assigns/keeps the record id.
    private int AddArmedLocked(ReminderRecord rec, long ms)
    {
        if (rec.Id <= 0) rec.Id = _nextId++;
        else if (rec.Id >= _nextId) _nextId = rec.Id + 1;
        var entry = new Entry { Record = rec };
        entry.Timer = new Timer(OnFire, entry, Timeout.Infinite, Timeout.Infinite);
        _entries.Add(entry);
        entry.Timer.Change(ms, Timeout.Infinite);
        return rec.Id;
    }

    private void OnFire(object? state)
    {
        var entry = (Entry)state!;
        bool recurring;
        string message;
        lock (_gate)
        {
            // If a Cancel/Clear already removed it (raced with the timer callback), do nothing.
            if (!_entries.Contains(entry)) return;
            var rec = entry.Record;
            message = rec.Message;
            recurring = rec.Kind != ReminderKind.Once;
            if (!recurring) _entries.Remove(entry);
            else
            {
                var now = DateTime.Now;
                rec.DueAt = rec.NextOccurrence(now);          // strictly future (NextOccurrence uses <=)
                entry.Timer!.Change(ArmMs(rec.DueAt - now), Timeout.Infinite); // re-arm the SAME timer
            }
        }
        if (!recurring) entry.Timer?.Dispose();

        var pet = _pet();
        if (pet is not null) _ = AnnounceAsync(pet, message); // fire-and-forget; never blocks the timer thread
        _onChanged?.Invoke(); // recurring: due time advanced; once: reminder removed — persist either way
    }

    /// <summary>Announce a fired reminder: show the speech bubble (held, movement frozen) and play a random
    /// one-shot action so the pet visibly reacts when the reminder comes due. The action is picked from the
    /// worn character's supported actions and forced to "once" so it's a transient gesture, not a held pose;
    /// if the pet can't act right now (e.g. mid-fall) the control just declines and only the bubble shows.
    /// All best-effort — a failure here must never crash the timer thread.</summary>
    private static async Task AnnounceAsync(IPetControl pet, string message)
    {
        try
        {
            string text = "⏰ " + message; // ⏰
            // Hold the bubble (and freeze wandering) long enough to read; scales with message length.
            double secs = Math.Clamp(6 + message.Length * 0.07, 6, 30);
            await pet.Say(text, secs, freezeMovement: true).ConfigureAwait(false);

            var caps = await pet.GetCapabilities().ConfigureAwait(false);
            if (caps.Actions.Count > 0)
            {
                string action = caps.Actions[Random.Shared.Next(caps.Actions.Count)].Name;
                await pet.DoAction(action, "once").ConfigureAwait(false);
            }
        }
        catch { /* best-effort reaction; never propagate out of the timer callback */ }
    }

    private static long ArmMs(TimeSpan span) =>
        span <= TimeSpan.Zero ? 0 : (long)Math.Min(span.TotalMilliseconds, MaxTimerMs);

    private static ReminderRecord Copy(ReminderRecord r) => new()
    {
        Id = r.Id, Kind = r.Kind, Message = r.Message, DueAt = r.DueAt,
        TimeOfDay = r.TimeOfDay, Weekday = r.Weekday, DayOfMonth = r.DayOfMonth,
    };
}
