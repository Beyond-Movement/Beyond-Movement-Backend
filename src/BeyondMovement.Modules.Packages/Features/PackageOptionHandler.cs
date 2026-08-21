using BeyondMovement.Modules.Packages.Contracts;
using BeyondMovement.Modules.Packages.Domain;
using BeyondMovement.Modules.Packages.Persistence;
using BeyondMovement.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace BeyondMovement.Modules.Packages.Features;

/// <summary>
/// The Admin's catalogue: list, read, create, edit, archive, restore. Never delete — a package
/// option the coach withdraws is still the option somebody bought, and the purchase history
/// arriving in a later phase has to be able to name it.
/// </summary>
public sealed class PackageOptionHandler(IPackagesDbContext db, IClock clock)
{
    public async Task<IReadOnlyList<PackageOptionResponse>> ListAsync(
        Guid coachId, bool archived, CancellationToken ct = default)
    {
        var options = await Query(coachId)
            .Where(o => o.IsArchived == archived)
            .OrderBy(o => o.Name)
            .ThenBy(o => o.Id)
            .ToListAsync(ct);

        return [.. options.Select(o => o.ToResponse())];
    }

    public async Task<Result<PackageOptionResponse>> GetAsync(
        Guid coachId, Guid id, CancellationToken ct = default)
    {
        var option = await Query(coachId).FirstOrDefaultAsync(o => o.Id == id, ct);

        return option is null
            ? Result<PackageOptionResponse>.Failure(PackageErrors.NotFound)
            : Result<PackageOptionResponse>.Success(option.ToResponse());
    }

    public async Task<Result<PackageOptionResponse>> CreateAsync(
        Guid coachId, SavePackageOptionRequest request, CancellationToken ct = default)
    {
        if (await NameTakenAsync(coachId, request.Name, excluding: null, ct))
            return Result<PackageOptionResponse>.Failure(PackageErrors.NameConflict);

        var option = PackageOption.Create(
            coachId, request.Name, request.Sessions, request.DefaultPriceMinor,
            request.Features, clock.UtcNow);

        db.PackageOptions.Add(option);
        await db.SaveChangesAsync(ct);

        return Result<PackageOptionResponse>.Success(option.ToResponse());
    }

    public async Task<Result<PackageOptionResponse>> EditAsync(
        Guid coachId, Guid id, EditPackageOptionRequest request, CancellationToken ct = default)
    {
        var option = await TrackedAsync(coachId, id, ct);

        if (option is null)
            return Result<PackageOptionResponse>.Failure(PackageErrors.NotFound);

        if (option.Version != request.Version)
            return Result<PackageOptionResponse>.Failure(PackageErrors.ConcurrencyConflict);

        if (await NameTakenAsync(coachId, request.Name, excluding: id, ct))
            return Result<PackageOptionResponse>.Failure(PackageErrors.NameConflict);

        var edited = option.Edit(
            request.Name, request.Sessions, request.DefaultPriceMinor, request.Features, clock.UtcNow);

        if (edited.IsFailure)
            return Result<PackageOptionResponse>.Failure(edited.Error!);

        await db.SaveChangesAsync(ct);

        return Result<PackageOptionResponse>.Success(option.ToResponse());
    }

    public Task<Result<PackageOptionResponse>> ArchiveAsync(
        Guid coachId, Guid id, int version, CancellationToken ct = default) =>
        ChangeStateAsync(coachId, id, version, (o, now) => o.Archive(now), ct);

    public Task<Result<PackageOptionResponse>> RestoreAsync(
        Guid coachId, Guid id, int version, CancellationToken ct = default) =>
        ChangeStateAsync(coachId, id, version, (o, now) => o.Restore(now), ct);

    private async Task<Result<PackageOptionResponse>> ChangeStateAsync(
        Guid coachId, Guid id, int version, Func<PackageOption, DateTime, Result> change,
        CancellationToken ct)
    {
        var option = await TrackedAsync(coachId, id, ct);

        if (option is null)
            return Result<PackageOptionResponse>.Failure(PackageErrors.NotFound);

        if (option.Version != version)
            return Result<PackageOptionResponse>.Failure(PackageErrors.ConcurrencyConflict);

        var result = change(option, clock.UtcNow);

        if (result.IsFailure)
            return Result<PackageOptionResponse>.Failure(result.Error!);

        await db.SaveChangesAsync(ct);

        return Result<PackageOptionResponse>.Success(option.ToResponse());
    }

    /// <summary>
    /// Scoped to the coach on every read. An option belonging to somebody else must be a 404 and
    /// not a 403, so the API never confirms that an id it will not serve exists (CLAUDE.md 6).
    /// </summary>
    private IQueryable<PackageOption> Query(Guid coachId) =>
        db.PackageOptions
            .AsNoTracking()
            .Include(PackageOption.FeaturesNavigation)
            .Where(o => o.CoachId == coachId);

    private Task<PackageOption?> TrackedAsync(Guid coachId, Guid id, CancellationToken ct) =>
        db.PackageOptions
            .Include(PackageOption.FeaturesNavigation)
            .FirstOrDefaultAsync(o => o.Id == id && o.CoachId == coachId, ct);

    /// <summary>
    /// Checked here so the caller gets PACKAGE_NAME_CONFLICT rather than a database exception.
    /// The unique index is still the thing that makes it true under a race; this is the polite
    /// path, not the guarantee.
    /// </summary>
    private Task<bool> NameTakenAsync(Guid coachId, string name, Guid? excluding, CancellationToken ct)
    {
        var normalized = name.Trim();

        return db.PackageOptions.AnyAsync(o =>
            o.CoachId == coachId
            && !o.IsArchived
            && o.Id != excluding
            && o.Name.ToLower() == normalized.ToLower(), ct);
    }
}

public static class PackageOptionMappings
{
    public static PackageOptionResponse ToResponse(this PackageOption option) => new(
        option.Id,
        option.Name,
        option.Sessions,
        option.DefaultPriceMinor,
        Currency.Egp,
        [.. option.OrderedFeatures.Select(f => f.Text)],
        option.IsArchived,
        option.ArchivedAtUtc,
        option.Version,
        option.CreatedAtUtc,
        option.UpdatedAtUtc);
}
