namespace IndxServer.Services
{
    /// <summary>
    /// What the dataset's owners changed, recorded beside what its visitors did so the Statistics
    /// tab can say why its numbers moved (Notes/statistics-design.md, "Change events"). The names
    /// are stored as they are, so they are the format: add, never rename.
    /// </summary>
    public static class DatasetChangeKind
    {
        public const string Synonyms = "synonyms";
        public const string BoostRules = "boostRules";
        public const string Fields = "fields";
        public const string Reindex = "reindex";
        public const string Replace = "replace";
        public const string Documents = "documents";
        public const string Hibernate = "hibernate";
        public const string Wake = "wake";
        public const string Rename = "rename";
        public const string Delete = "delete";
    }

    /// <summary>
    /// Where a change is reported. The engine registry and the stores call it at the one place
    /// every route to a change passes through; <see cref="StatisticsService"/> is the only
    /// implementation and attaches itself at startup, so with statistics off nothing is attached
    /// and nothing is recorded. Never who made the change, never content: counts and field names.
    /// </summary>
    public interface IDatasetChangeSink
    {
        /// <param name="teamId">The team that owns the dataset.</param>
        /// <param name="dataSet">The dataset's name, as statistics key it.</param>
        /// <param name="kind">One of <see cref="DatasetChangeKind"/>.</param>
        /// <param name="summary">A small object serialised as the event's JSON summary: counts
        /// before and after, field names. Null when the kind says it all.</param>
        void Changed(string teamId, string dataSet, string kind, object? summary = null);
    }
}
