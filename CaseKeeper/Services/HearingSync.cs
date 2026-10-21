using CaseKeeper.Data;
using CaseKeeper.Models;

namespace CaseKeeper.Services;

/// <summary>
/// Keeps a timeline entry in step with each hearing, so hearings appear in chronologies and on the
/// dashboard. Used by the Hearings screen and by approvals in the Import review screen, so both follow
/// exactly the same rule.
/// </summary>
public static class HearingSync
{
    public static void Sync(Database db, Hearing h)
    {
        var ev = h.EventId is { } id ? db.GetEvents().FirstOrDefault(e => e.Id == id) : null;
        ev ??= new TimelineEvent { EventType = "Hearing" };
        ev.EventDate = h.HearingDate ?? DateTime.Today;
        ev.Time = h.Time ?? "";
        ev.EventType = "Hearing";
        ev.Title = $"{(string.IsNullOrWhiteSpace(h.HearingType) ? "Hearing" : h.HearingType)}{(h.Status is "Adjourned" or "Vacated" ? $" ({h.Status.ToLowerInvariant()})" : "")}";
        ev.Description = string.Join("\n", new[]
        {
            string.Join(" · ", new[] { h.Court, string.IsNullOrWhiteSpace(h.Judge) ? null : "before " + h.Judge, h.Mode }.Where(s => !string.IsNullOrWhiteSpace(s))),
            string.IsNullOrWhiteSpace(h.Outcome) ? null : "Outcome: " + h.Outcome
        }.Where(s => !string.IsNullOrWhiteSpace(s)));
        ev.Location = h.Court ?? "";
        ev.DocumentId = h.DocumentId;
        bool isNew = ev.Id == 0;
        db.SaveEvent(ev);
        if (isNew || h.EventId != ev.Id)
        {
            h.EventId = ev.Id;
            db.Save(h);
        }
    }
}
