namespace BeyondMovement.SharedKernel;

/// <summary>
/// The package features this product <b>recognises by name</b> — the ones that unlock behaviour
/// rather than only describing it.
/// <para>
/// A package feature is normally just a line of text the coach wrote, and that stays true: see
/// <see cref="PackageFeature"/>. This enum names the small set that the backend acts on, so that
/// a rule is never decided by comparing display text. The coach may word the Observations line
/// however they like — "Observations", "Coach attends your competitions", Arabic, anything — and
/// eligibility still reads the code.
/// </para>
/// <para>
/// It lives in SharedKernel because three modules need the vocabulary and none may reference
/// another (CLAUDE.md section 4): Packages owns the catalogue entry and the purchased package,
/// Finance snapshots it onto the purchase, and the Observation Request rule in the Api reads it.
/// The same reason <see cref="Gender"/> lives here.
/// </para>
/// <para>
/// Serialised as its name, never as an integer, so rows and the generated client read the same
/// and reordering the members cannot silently remap stored data. <b>Treat it as an open set in
/// the app</b> — more codes will be added, and a client that throws on an unknown one breaks on
/// a backend deployment.
/// </para>
/// </summary>
public enum PackageFeatureCode
{
    /// <summary>
    /// The athlete may ask to be observed — <c>POST /me/observation-requests</c>. Enforced
    /// against the athlete's <b>active purchased package</b>, never against the catalogue entry,
    /// so editing the template does not change what somebody already bought.
    /// </summary>
    Observations
}

/// <summary>
/// One line on a package card: the text the coach wrote, and optionally a
/// <see cref="PackageFeatureCode"/> saying which recognised feature it is.
/// <para>
/// <b><see cref="Code"/> is null for an ordinary feature</b>, which is the overwhelming majority
/// of them and the original behaviour of this model: arbitrary text, as many as the coach wants
/// up to the limit, no meaning attached beyond what the athlete reads. Nothing about that is
/// restricted by this type.
/// </para>
/// <para>
/// <see cref="Text"/> is always the thing displayed and is always the coach's to change — it is
/// never read to decide anything. A non-null <see cref="Code"/> is the only machine-readable
/// identity, and at most one feature per package option may carry a given code.
/// </para>
/// </summary>
public sealed record PackageFeature(string Text, PackageFeatureCode? Code = null);
