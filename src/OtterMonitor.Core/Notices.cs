using System.Globalization;

namespace OtterMonitor.Core;

public enum NoticeKind { Info, Warning, Error }
public sealed record Notice(string Title, string Message, NoticeKind Kind);

/// <summary>
/// Turns repeated readings into one notice per real transition. It only reports changes it has seen happen
/// while the app was watching, so starting Otter Monitor never produces a burst of old news.
/// </summary>
public sealed class NoticeTracker
{
    public const double LowQuota = 15;
    private sealed class ServiceState { public bool WasGood, Alerted; public int BadReadings; }
    private readonly Dictionary<string, ServiceState> services = [];
    private readonly Dictionary<Guid, (Guid RunId, bool Running)> launches = [];
    private readonly Dictionary<string, double> quotas = [];

    /// <summary>One fresh probe reading. Two bad readings in a row (about 20 s) are required, so restarts and HMR reloads stay quiet.</summary>
    public Notice? Service(string key, string name, ServiceTone tone, string label)
    {
        if (!services.TryGetValue(key, out var state)) services[key] = state = new();
        if (tone != ServiceTone.Bad)
        {
            state.BadReadings = 0;
            if (tone != ServiceTone.Good) return null;
            state.WasGood = true;
            if (!state.Alerted) return null;
            state.Alerted = false;
            return new(name + UiText.T(" svarar igen"), label, NoticeKind.Info);
        }
        if (++state.BadReadings < 2 || !state.WasGood || state.Alerted) return null;
        state.Alerted = true;
        return new(name + " · " + label, UiText.T("Tjänsten svarade tidigare men gör det inte längre. Öppna Otter Monitor för detaljer."), NoticeKind.Error);
    }

    /// <summary>Drops services that no longer exist, so a new process on the same port starts fresh.</summary>
    public void KeepServices(IEnumerable<string> keys)
    {
        var keep = keys.ToHashSet();
        foreach (var key in services.Keys.Where(key => !keep.Contains(key)).ToList()) services.Remove(key);
    }

    /// <summary>A quick start that was seen running and then ended by itself with a failure code.</summary>
    public Notice? Launch(Guid id, string name, Guid? runId, bool running, bool stoppedByUser, uint? exitCode)
    {
        var seen = launches.TryGetValue(id, out var previous);
        if (runId is not { } run) { launches.Remove(id); return null; }
        launches[id] = (run, running);
        if (!seen || previous.RunId != run || !previous.Running || running || stoppedByUser || exitCode == 0) return null;
        return exitCode is { } code
            ? new(name + UiText.T(" misslyckades"), UiText.F("Kommandot avslutades med kod {0}. \u00D6ppna loggen f\u00F6r utdata.", code), NoticeKind.Error)
            : new(name + UiText.T(" har avslutats"), UiText.T("Kommandot är inte längre igång. Öppna loggen för utdata."), NoticeKind.Warning);
    }

    /// <summary>Notifies when a quota window drops to <see cref="LowQuota"/> % or lower, and when it is refilled after having been low.</summary>
    public IEnumerable<Notice> Quota(string assistant, UsageSnapshot? snapshot, DateTimeOffset now, TimeSpan maxAge)
    {
        if (snapshot is null || now - snapshot.ObservedAt > maxAge) yield break;
        var named = snapshot.Windows.Select(window => window.Name).Distinct().Count() > 1;
        foreach (var window in snapshot.Windows)
        {
            if (window.ResetsAt <= now) continue;
            var label = (named ? window.Name + " " : "") + window.Period;
            var key = assistant + "|" + label;
            var remaining = window.Remaining;
            var known = quotas.TryGetValue(key, out var previous);
            quotas[key] = remaining;
            if (!known) continue;
            var percent = remaining.ToString("0.#", CultureInfo.InvariantCulture) + " %";
            if (previous > LowQuota && remaining <= LowQuota)
                yield return new($"{assistant} · {percent} kvar ({label})", window.ResetsAt is { } reset ? UiText.F("\u00C5terst\u00E4lls {0:ddd HH:mm}.", reset.ToLocalTime()) : UiText.T("Återställningstid saknas."), NoticeKind.Warning);
            else if (previous <= LowQuota && remaining >= 50)
                yield return new(UiText.F("{0}-kvoten har \u00E5terst\u00E4llts ({1})", assistant, label), $"{percent} kvar.", NoticeKind.Info);
        }
    }
}
