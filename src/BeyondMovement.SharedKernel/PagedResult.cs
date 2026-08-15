namespace BeyondMovement.SharedKernel;

/// <summary>
/// One page of a list endpoint. Offset paging, not cursor paging: the athlete list is a
/// browsable, sortable table where the coach jumps to a page, not an append-only stream
/// (CLAUDE.md section 7 reserves cursors for messages, sessions and notifications).
/// </summary>
public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount)
{
    public const int MaxPageSize = 100;
    public const int DefaultPageSize = 20;

    public int TotalPages => TotalCount == 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);

    public bool HasNextPage => Page < TotalPages;
    public bool HasPreviousPage => Page > 1;

    /// <summary>Clamps caller-supplied paging so a hostile or careless page size cannot ask for everything.</summary>
    public static (int Page, int PageSize) Normalize(int? page, int? pageSize) =>
        (Math.Max(1, page ?? 1),
         Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize));
}
