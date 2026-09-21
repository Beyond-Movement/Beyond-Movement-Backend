namespace BeyondMovement.Modules.Scheduling.Domain;

/// <summary>
/// A coach's note about one session. Written on Session Details and read back two ways: the
/// running record of a single session, and the athlete's Session Notes History.
/// <para>
/// Several notes per session rather than one editable blob: the screen offers add <em>and</em>
/// edit, an author is recorded on each, and a history that can only ever be overwritten loses
/// what the coach wrote last week the first time they add a line this week. <b>There is
/// deliberately no unique constraint on <see cref="SessionId"/></b> — the product may normally
/// produce one note per session, but the model must keep supporting several.
/// </para>
/// <para>
/// <b>Notes are shared with the athlete</b> (client decision, 2026-09-21). They were Admin-only
/// when they were introduced; the athlete now reads their own through <c>GET /me/notes</c>, and
/// that applies to every note, including those written before the decision. There is no private
/// note and no per-note visibility — a coach writing one should assume the athlete will read it.
/// </para>
/// </summary>
public sealed class SessionNote
{
    public const int MaxContentLength = 4000;

    /// <summary>
    /// Long enough for a descriptive sentence, short enough that a history row can show it whole.
    /// Compare <c>PackageOption.MaxNameLength</c> at 100: a title is name-shaped, but this one is
    /// more often a phrase than a label.
    /// </summary>
    public const int MaxTitleLength = 200;

    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid SessionId { get; private set; }

    /// <summary>Who wrote it. Only the Admin can — the write endpoints are Admin-only.</summary>
    public Guid AuthorUserId { get; private set; }

    /// <summary>
    /// What the note is called, and what a history row shows before anything else. Required: a
    /// list of notes that all read the same is a list nobody can scan.
    /// <para>
    /// Notes written before titles existed were backfilled from their own first line, so the
    /// title of an old note is a shortened restatement of what the coach actually wrote rather
    /// than something invented for it — and the coach can rewrite it like any other.
    /// </para>
    /// </summary>
    public string Title { get; private set; } = null!;

    public string Content { get; private set; } = null!;
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    private SessionNote() { }   // EF Core

    public static SessionNote Write(
        Guid sessionId, Guid authorUserId, string title, string content, DateTime nowUtc) => new()
    {
        SessionId = sessionId,
        AuthorUserId = authorUserId,
        Title = title.Trim(),
        Content = content.Trim(),
        CreatedAtUtc = nowUtc,
        UpdatedAtUtc = nowUtc
    };

    /// <summary>
    /// Rewrites the title and the text together, because the screen edits them together and there
    /// is no order of two setters that cannot leave the note half-changed.
    /// <para>
    /// <see cref="CreatedAtUtc"/> deliberately stays put, so the history keeps its order when a
    /// note written days ago is corrected today; <see cref="UpdatedAtUtc"/> is what moves, and is
    /// how a client shows that a note was edited.
    /// </para>
    /// </summary>
    public void Revise(string title, string content, DateTime nowUtc)
    {
        Title = title.Trim();
        Content = content.Trim();
        UpdatedAtUtc = nowUtc;
    }
}
