namespace Jellyfin.Plugin.Tagsmith.Collections;

/// <summary>
/// The membership change needed to bring a collection in line with the tag set.
/// </summary>
/// <param name="Add">Items to add.</param>
/// <param name="Remove">Items to remove.</param>
public readonly record struct MemberChange(IReadOnlyList<Guid> Add, IReadOnlyList<Guid> Remove)
{
    /// <summary>Gets a value indicating whether anything needs writing.</summary>
    public bool IsEmpty => Add.Count == 0 && Remove.Count == 0;
}

/// <summary>
/// Diffs a collection's current members against what the tags say they should be.
/// </summary>
/// <remarks>
/// Steady state is the common case — a few dozen set comparisons and zero writes — so this
/// exists to make "nothing changed" cheap and obvious, and to keep the decision testable
/// away from anything that touches the library database.
/// </remarks>
public static class MemberDiff
{
    /// <summary>
    /// Returns what to add and what to remove.
    /// </summary>
    /// <param name="current">The collection's current members.</param>
    /// <param name="wanted">The members the tags call for.</param>
    public static MemberChange Between(IEnumerable<Guid> current, IEnumerable<Guid> wanted)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(wanted);

        var have = current.ToHashSet();
        var want = wanted.ToHashSet();

        var add = want.Where(id => !have.Contains(id)).ToArray();
        var remove = have.Where(id => !want.Contains(id)).ToArray();

        return new MemberChange(add, remove);
    }
}
