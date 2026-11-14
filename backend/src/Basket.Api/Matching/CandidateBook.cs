using Basket.Api.Data;
using Basket.Api.Prices;

namespace Basket.Api.Matching;

/// <summary>A page the matcher passed: its address, the title and barcode it came with and the verdict.</summary>
public sealed record Proposal(string Url, string? Title, string? Barcode, MatchVerdict Verdict);

/// <summary>
/// The one place proposals are written, so a search posted to the endpoint and the server's own run over the catalog keep
/// the same rules: a page posted again updates its row, and each product keeps at most <see cref="MatchEndpoints.MaxProposalsPerStore"/>
/// open proposals per shop, the strongest of what was already open and what just passed. Rejected rows are never touched.
/// </summary>
public static class CandidateBook
{
    /// <summary>
    /// Applies <paramref name="passed"/> (addresses already normalized, none rejected or already linked) to the context for one product
    /// and shop; the caller saves. <paramref name="all"/> holds every row of that product and shop by address, rejected ones included.
    /// Returns the addresses that did not make the cut.
    /// </summary>
    public static IReadOnlySet<string> Apply(
        AppDbContext db,
        Guid productId,
        Store store,
        Dictionary<string, MatchCandidate> all,
        IReadOnlyList<Proposal> passed,
        DateTimeOffset now)
    {
        var existing = all.Values.Where(c => c.Status == CandidateStatus.Proposed).ToList();
        var rows = new List<(MatchCandidate Row, bool IsNew, string Url)>();
        foreach (var proposal in passed)
        {
            if (all.TryGetValue(proposal.Url, out var known) && known.Status == CandidateStatus.Rejected)
            {
                continue;
            }

            var isNew = known is null;
            var row = known ?? new MatchCandidate { Id = Guid.NewGuid(), ProductId = productId, Store = store, Url = proposal.Url, ProposedAt = now, Status = CandidateStatus.Proposed };
            row.Title = proposal.Title;
            row.Barcode = Gtin.TryNormalize(proposal.Barcode, out var code) ? code : row.Barcode;
            row.Basis = proposal.Verdict.Basis;
            row.Score = proposal.Verdict.Score;
            rows.Add((row, isNew, proposal.Url));
        }

        // The open proposals for this shop are the best few of what was already open and what just passed.
        var open = existing.Concat(rows.Where(r => r.IsNew).Select(r => r.Row)).ToList();
        var keep = open.OrderByDescending(c => c.Score).ThenBy(c => c.ProposedAt).ThenBy(c => c.Url, StringComparer.Ordinal)
            .Take(MatchEndpoints.MaxProposalsPerStore).ToHashSet();
        var outranked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (row, isNew, url) in rows)
        {
            if (!keep.Contains(row))
            {
                outranked.Add(url);
            }
            else if (isNew)
            {
                db.MatchCandidates.Add(row);
            }
        }

        db.MatchCandidates.RemoveRange(existing.Where(c => !keep.Contains(c)));
        return outranked;
    }
}
