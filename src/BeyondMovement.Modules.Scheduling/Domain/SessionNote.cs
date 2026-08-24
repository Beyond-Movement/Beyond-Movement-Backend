namespace BeyondMovement.Modules.Scheduling.Domain;

/// <summary>
/// A coach's note about one session. The UI/UX document places these on Session Details (Admin
/// View) and says they "become part of the athlete's overall Whiteboard &amp; Notes history",
/// so they are stored per session and read back per athlete.
/// <para>
/// Several notes per session rather than one editable blob: the screen offers add <em>and</em>
/// edit, an author is recorded on each, and a history that can only ever be overwritten loses
/// what the coach wrote last week the first time they add a line this week.
/// </para>
/// </summary>
public sealed class SessionNote
{
    public const int MaxContentLength = 4000;

    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid SessionId { get; private set; }

    /// <summary>Who wrote it. Only the Admin can, today — the endpoints are Admin-only.</summary>
    public Guid AuthorUserId { get; private set; }

    public string Content { get; private set; } = null!;
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    private SessionNote() { }   // EF Core

    public static SessionNote Write(Guid sessionId, Guid authorUserId, string content, DateTime nowUtc) => new()
    {
        SessionId = sessionId,
        AuthorUserId = authorUserId,
        Content = content.Trim(),
        CreatedAtUtc = nowUtc,
        UpdatedAtUtc = nowUtc
    };

    /// <summary>
    /// Rewrites the text. <see cref="CreatedAtUtc"/> deliberately stays put, so the history keeps
    /// its order when a note written days ago is corrected today.
    /// </summary>
    public void Revise(string content, DateTime nowUtc)
    {
        Content = content.Trim();
        UpdatedAtUtc = nowUtc;
    }
}
