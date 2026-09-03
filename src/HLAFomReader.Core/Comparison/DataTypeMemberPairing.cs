using System;
using System.Collections.Generic;

namespace HLAFomReader.Core.Comparison;

/// <summary>
/// One line of a paired walk through two datatypes: a member of the A side beside the member of the
/// B side it was lined up with, or one of the two alone.
/// </summary>
/// <param name="Left">The A member, or null when B has one A does not.</param>
/// <param name="Right">The B member, or null when A has one B does not.</param>
/// <param name="PairedByPosition">
/// True when the two were paired on their <b>position</b> rather than on their name — the reader has
/// to know which pairings the FOM asserts and which this inferred.
/// </param>
public readonly record struct DataTypeMemberPair(
    DataTypeDetailMember? Left,
    DataTypeDetailMember? Right,
    bool PairedByPosition);

/// <summary>
/// Lines the members of one datatype up against the members of another, so the two can be read side
/// by side a level at a time.
/// </summary>
/// <remarks>
/// <para>
/// This is the one genuinely new judgement in the side-by-side worksheet, and it is deliberately
/// <b>asymmetric</b> across the member roles, because what the wire guarantees differs by role.
/// </para>
/// <para>
/// A record's <b>field order places every byte</b>. <see cref="DataTypeResolver"/> makes exactly this
/// point by keeping field order in the canonical form and throwing field names away: two records
/// with the same field types in the same order encode identically however the fields are spelled. So
/// when names fail to match, position is real evidence rather than a guess, and the third field of
/// A genuinely does occupy the bytes the third field of B occupies. A version step that renames
/// <c>X</c>/<c>Y</c>/<c>Z</c> to <c>XPos</c>/<c>YPos</c>/<c>ZPos</c> is the ordinary case, and pairing
/// those by position is the whole point of the sheet.
/// </para>
/// <para>
/// Attributes carry no such guarantee — HLA transmits handles, and declaration order says nothing —
/// which is why this is never applied above the datatype level. The attribute pairing stays with
/// <see cref="AttributeMapper"/>, on names alone.
/// </para>
/// <para>
/// Nothing is dropped and nothing is invented: a member that finds no counterpart comes back as a
/// pair with one half null, and every positional pairing is flagged so the reader can discount it.
/// </para>
/// </remarks>
public static class DataTypeMemberPairing
{
    /// <summary>
    /// Pairs the members of two datatypes of the same structural family.
    /// </summary>
    /// <param name="left">A's members, in declaration order. Null is treated as empty.</param>
    /// <param name="right">B's members, in declaration order. Null is treated as empty.</param>
    /// <param name="options">Name folding; strict defaults are used when null.</param>
    /// <returns>
    /// The pairs in A's declaration order, with B's unmatched members appended in B's own order —
    /// the same rule <see cref="AttributeMapper"/> uses for attributes, so the two read alike.
    /// </returns>
    public static IReadOnlyList<DataTypeMemberPair> Pair(
        IReadOnlyList<DataTypeDetailMember>? left,
        IReadOnlyList<DataTypeDetailMember>? right,
        ComparisonOptions? options = null)
    {
        var a = left ?? Array.Empty<DataTypeDetailMember>();
        var b = right ?? Array.Empty<DataTypeDetailMember>();
        var o = options ?? new ComparisonOptions();

        var pairs = new List<DataTypeMemberPair>(Math.Max(a.Count, b.Count));

        if (a.Count == 0 && b.Count == 0) return pairs;

        // One side empty: everything on the other side stands alone. Taken early so the passes
        // below never have to special-case it.
        if (a.Count == 0)
        {
            foreach (var member in b) pairs.Add(new DataTypeMemberPair(null, member, false));
            return pairs;
        }

        if (b.Count == 0)
        {
            foreach (var member in a) pairs.Add(new DataTypeMemberPair(member, null, false));
            return pairs;
        }

        // An array's element is the type of every slot rather than a named thing, so the two sides'
        // "names" here are type names — float64 against double — and matching on them would split
        // one element into two half-rows that say nothing. Index 0 to index 0, unconditionally.
        if (IsElementOnly(a) && IsElementOnly(b))
        {
            pairs.Add(new DataTypeMemberPair(a[0], b[0], false));
            return pairs;
        }

        var matchedRight = new bool[b.Count];
        var partner = new int[a.Count];
        Array.Fill(partner, -1);

        // ---- pass 1: by name, folded exactly as the rest of the app folds names ----------------
        //
        // First sighting wins. A record declaring the same field name twice is malformed, and
        // pairing the second occurrence to the first B field of that name would be arbitrary.
        var byName = new Dictionary<string, int>(o.NameComparer);
        for (var i = 0; i < b.Count; i++)
        {
            var key = Key(b[i], o);
            if (key.Length != 0) byName.TryAdd(key, i);
        }

        for (var i = 0; i < a.Count; i++)
        {
            var key = Key(a[i], o);
            if (key.Length == 0) continue;

            if (!byName.TryGetValue(key, out var j) || matchedRight[j]) continue;
            if (a[i].Role != b[j].Role) continue;

            partner[i] = j;
            matchedRight[j] = true;
        }

        // ---- pass 2: by position, for the roles where position is on the wire -------------------
        var residualRight = new List<int>();
        for (var j = 0; j < b.Count; j++)
            if (!matchedRight[j] && IsPositional(b[j].Role)) residualRight.Add(j);

        var next = 0;
        var byPosition = new HashSet<int>();

        for (var i = 0; i < a.Count && next < residualRight.Count; i++)
        {
            if (partner[i] >= 0 || !IsPositional(a[i].Role)) continue;

            var j = residualRight[next++];

            // Zipped in declaration order, and only within one role: pairing a discriminant against
            // a field because both happened to be left over would assert something the FOM does not.
            if (a[i].Role != b[j].Role)
            {
                next--;
                continue;
            }

            partner[i] = j;
            matchedRight[j] = true;
            byPosition.Add(i);
        }

        // ---- emit --------------------------------------------------------------------------
        for (var i = 0; i < a.Count; i++)
        {
            var j = partner[i];
            pairs.Add(j < 0
                ? new DataTypeMemberPair(a[i], null, false)
                : new DataTypeMemberPair(a[i], b[j], byPosition.Contains(i)));
        }

        for (var j = 0; j < b.Count; j++)
            if (!matchedRight[j]) pairs.Add(new DataTypeMemberPair(null, b[j], false));

        return pairs;
    }

    /// <summary>The note put on a pair the FOM did not name-match, so the reader can discount it.</summary>
    public const string PositionalNote = "paired by position; the two names differ";

    /// <summary>
    /// Roles whose declaration order is part of the encoding, and which may therefore be paired
    /// positionally when their names disagree.
    /// </summary>
    /// <remarks>
    /// Fields only. A variant's alternatives are selected by their discriminant value rather than by
    /// their position, so two alternatives that share an index but not a selector are unrelated;
    /// enumerators are values rather than layout; a representation is the single thing the type is
    /// carried in and can only ever pair with the other side's.
    /// </remarks>
    private static bool IsPositional(DataTypeMemberRole role) => role == DataTypeMemberRole.Field;

    /// <summary>True for a member list that is a single array element and nothing else.</summary>
    private static bool IsElementOnly(IReadOnlyList<DataTypeDetailMember> members) =>
        members.Count == 1 && members[0].Role == DataTypeMemberRole.Element;

    /// <summary>
    /// What a member is lined up on: its name, or for an alternative the enumerator that selects it.
    /// </summary>
    /// <remarks>
    /// An alternative's identity is the discriminant value it answers to, not the name of the field
    /// carrying it: rename the field and the same value still selects the same bytes. An enumerator
    /// is keyed on its name too, because its label is what a reader recognises, and the values are
    /// shown beside it either way.
    /// </remarks>
    private static string Key(DataTypeDetailMember member, ComparisonOptions options)
    {
        if (member.Role == DataTypeMemberRole.Alternative && !string.IsNullOrWhiteSpace(member.Value))
            return OmtNormalizer.NormalizeName(member.Value, options)?.Trim() ?? "";

        // A representation is one per type, so any two pair with each other whatever they are named.
        if (member.Role == DataTypeMemberRole.Representation) return " representation";

        // A discriminant is likewise one per variant record.
        if (member.Role == DataTypeMemberRole.Discriminant) return " discriminant";

        return OmtNormalizer.NormalizeName(member.Name, options)?.Trim() ?? "";
    }
}
