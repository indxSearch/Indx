using Indx.Api;

namespace IndxServer.Services.Observr
{
    /// <summary>A field as the records report found it: how many records lack a value, how many
    /// distinct values it holds (<see cref="DistinctCapped"/> when the count stopped at its cap).</summary>
    public sealed record ObservrRecordsField(string Name, bool Searched, long Without, long Distinct, bool DistinctCapped);

    /// <summary>
    /// The records report (the Records tab's "Scan the records", <see cref="DataCheck"/>) as Observr
    /// reads it: when the scan ran, how many records it read, how it stands now
    /// (<c>current</c>, <c>changed</c>: some records changed since, <c>outdated</c>: replaced, the
    /// searched fields changed, or more than 5% changed, with <see cref="OutdatedBecause"/>), how many
    /// records have searched text cut at indexing, and the fields, searched ones first and the most
    /// records without a value first. Observr never starts a scan: it reads one the owner ran.
    /// Counts and field names only, no record.
    /// </summary>
    public sealed record ObservrRecordsReport(DateTime CheckedUtc, long Records, string Freshness, string? OutdatedBecause, long ChangedSince,
        long Cut, List<ObservrRecordsField> Fields)
    {
        /// <summary>The fields sent: a report of a dataset with hundreds of fields stays small.</summary>
        public const int MaxFields = 40;

        /// <summary>The standing report of a dataset, or null when it was never scanned. The fields
        /// as they are now come from <paramref name="configuration"/> (the engine's, when it is
        /// loaded): without it the report's own fingerprint cannot be checked, so a report that was
        /// not replaced and has changed little reads as changed rather than current.</summary>
        public static ObservrRecordsReport? From(DataCheckStanding? standing, IReadOnlyList<FieldProxy>? configuration)
        {
            if (standing == null) return null;
            string freshness;
            string? because;
            if (configuration != null)
            {
                var fingerprint = DataCheck.Fingerprint(configuration.Select(f => new DataCheck.CheckedField(f.FieldName, f.WordMatching == true || f.PatternMatching == true)),
                    DataCheck.TextCeiling);
                because = standing.OutdatedBecause(fingerprint);
                freshness = standing.FreshnessFor(fingerprint) switch
                {
                    DataCheckFreshness.Current => "current",
                    DataCheckFreshness.Changed => "changed",
                    _ => "outdated",
                };
            }
            else
            {
                because = standing.OutdatedBecause(standing.Report.Fingerprint);
                freshness = because != null ? "outdated" : "changed";
            }
            var r = standing.Report;
            var fields = r.Fields.OrderByDescending(f => f.Searched).ThenByDescending(f => f.Without).ThenBy(f => f.Name, StringComparer.Ordinal)
                .Take(MaxFields).Select(f => new ObservrRecordsField(f.Name, f.Searched, f.Without, f.Distinct, f.DistinctCapped)).ToList();
            return new ObservrRecordsReport(r.CheckedUtc, r.Records, freshness, because, standing.ChangedSince, r.Cut, fields);
        }

        /// <summary>The report as the chat reads it: a few lines, the findings first.</summary>
        public string Describe()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Records report from {CheckedUtc:yyyy-MM-dd HH:mm} UTC, {Records:N0} records read; "
                + Freshness switch
                {
                    "current" => "no record has changed since, so it holds exactly.",
                    "changed" => $"{ChangedSince:N0} records have changed since, so the findings hold and the totals have drifted a little.",
                    _ => $"it no longer holds: {OutdatedBecause ?? "too much has changed"}. Say so, and that a new scan on the Records tab would tell.",
                });
            sb.AppendLine(Cut == 0 ? "No searched text is cut at indexing." : $"{Cut:N0} records have searched text cut at indexing: only its start can be found.");
            foreach (var f in Fields)
            {
                var share = Records > 0 ? f.Without * 100.0 / Records : 0;
                sb.AppendLine($"- {f.Name} ({(f.Searched ? "searched" : "not searched")}): "
                    + (f.Without == 0 ? "every record has a value" : $"{f.Without:N0} records without a value ({(share is > 0 and < 1 ? "under 1" : Math.Round(share).ToString())}%)")
                    + $", {f.Distinct:N0}{(f.DistinctCapped ? "+" : "")} distinct values");
            }
            return sb.ToString();
        }
    }
}
