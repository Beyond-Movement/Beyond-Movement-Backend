using BeyondMovement.Infrastructure;
using BeyondMovement.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace BeyondMovement.Api.Packages;

/// <summary>
/// The feature lines a purchased package was sold with — the text the athlete actually read,
/// with the recognised code each line carried.
/// <para>
/// <b>Why this needs a reader at all.</b> A <c>PurchasedPackage</c> stores only the recognised
/// <c>PackageFeatureCode</c>s, because codes are the one thing eligibility rules consult and text
/// is never read to decide anything. The display text lives on the <c>PackagePurchase</c> that
/// produced the package, in the <c>PackagePurchaseFeatures</c> child table, where it was
/// snapshotted at the moment of sale. Both halves are already persisted; neither is derived from
/// the catalogue.
/// </para>
/// <para>
/// Packages and Finance are separate modules and neither may reference the other (CLAUDE.md
/// section 4), so the join between them lives here in the composition root — the same arrangement
/// <c>PurchaseReader</c> and <c>CatalogueReader</c> use, and it is read-only.
/// </para>
/// <para>
/// <b>Never the catalogue.</b> Nothing here touches <c>PackageOptions</c>. Renaming, rewording or
/// archiving a catalogue entry cannot change what an existing package reports, which is the whole
/// reason the snapshot exists.
/// </para>
/// <para>
/// <b>One query, however many packages.</b> The paged history asks for a hundred at a time, so
/// the lookup is by set rather than per row; nothing here runs a query per package.
/// </para>
/// </summary>
public sealed class PurchasedPackageFeatureReader(AppDbContext db)
{
    /// <summary>
    /// The lines for one package, in the order the athlete read them.
    /// <para>
    /// <b>Empty is a real answer</b>, not a failure: every package bought before the purchase
    /// record existed was backfilled with an empty snapshot, deliberately, because the catalogue
    /// may have been edited since and copying it then would have fabricated a card nobody was
    /// shown. The same is true of <c>includedFeatures</c>, and for the same reason.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<PackageFeature>> ForAsync(
        Guid purchasedPackageId, CancellationToken ct = default)
    {
        var byPackage = await ForManyAsync([purchasedPackageId], ct);

        return byPackage.TryGetValue(purchasedPackageId, out var features) ? features : [];
    }

    /// <summary>
    /// The lines for a set of packages, keyed by package id. A package with no snapshot is absent
    /// from the dictionary rather than present with an empty list, so callers use
    /// <see cref="Lookup"/> below and get <c>[]</c> either way.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<PackageFeature>>> ForManyAsync(
        IReadOnlyCollection<Guid> purchasedPackageIds, CancellationToken ct = default)
    {
        if (purchasedPackageIds.Count == 0)
            return new Dictionary<Guid, IReadOnlyList<PackageFeature>>();

        // Joined and ordered in the database. The unique filtered index on
        // PackagePurchases.PurchasedPackageId serves the lookup, and it also guarantees at most
        // one purchase per package, so a package cannot collect two sets of lines.
        var rows = await (
                from purchase in db.PackagePurchases.AsNoTracking()
                where purchase.PurchasedPackageId != null
                      && purchasedPackageIds.Contains(purchase.PurchasedPackageId.Value)
                join feature in db.PackagePurchaseFeatures.AsNoTracking()
                    on purchase.Id equals feature.PackagePurchaseId
                orderby feature.Position
                select new
                {
                    PackageId = purchase.PurchasedPackageId!.Value,
                    feature.Text,
                    feature.Code
                })
            .ToListAsync(ct);

        // GroupBy preserves the order within each group, so the Position ordering above survives.
        // Order is meaning on a package card: it is the order the coach wrote the lines in.
        return rows
            .GroupBy(x => x.PackageId)
            .ToDictionary(
                group => group.Key,
                IReadOnlyList<PackageFeature> (group) =>
                    [.. group.Select(x => new PackageFeature(x.Text, x.Code))]);
    }

    /// <summary>
    /// Turns the dictionary into the total function every mapping call site wants: a package with
    /// no snapshot reads as no lines rather than as a missing key.
    /// </summary>
    public static IReadOnlyList<PackageFeature> Lookup(
        IReadOnlyDictionary<Guid, IReadOnlyList<PackageFeature>> byPackage, Guid purchasedPackageId) =>
        byPackage.TryGetValue(purchasedPackageId, out var features) ? features : [];
}
