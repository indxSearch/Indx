using System.IO.Compression;
using Indx.Api;
using Indx.Core;
using Indx.Utilities;
using IndxServer.Engine;
using Microsoft.Extensions.Logging;

namespace IndxServer.Services
{
    /// <summary>
    /// "Start from a demo" (Nov 2026): a dataset a new team can have in one click, to try Indx before
    /// it has data of its own. Films from Wikidata, CC0, so it may ship with the server and be used
    /// for anything (TMDB's data, which the demos used before, is non-commercial only). About 25,000
    /// films with ten or more Wikipedia articles, built by tools/demo-data/build-films.py and embedded
    /// gzipped. The dataset is created, configured, loaded and indexed here, and ends Ready.
    /// </summary>
    public sealed class DemoDatasets(DatasetMetadataStore metadata, ILogger<DemoDatasets> logger)
    {
        /// <summary>The name it gets, or with a number after it when a dataset of that name exists.</summary>
        public const string FilmsName = "films";

        internal const string FilmsResource = "IndxServer.Demo.films.json.gz";

        /// <summary>The dataset's description, which the site description of Search or ask and
        /// Observr read too.</summary>
        public const string FilmsDescription =
            "Films from Wikidata (CC0): about 25,000 well-known films with their year, directors, main cast, genres, " +
            "countries, languages and running time. A demo dataset to try Indx with.";

        /// <summary>
        /// The field roles: the title first and heaviest, the original title beside it, then the
        /// people, then Wikidata's one-line description. Genres, countries and languages are what to
        /// narrow by; year and running time can also be sorted and ranged. One k1 for every
        /// searchable field, so the dataset takes the BM25F path, as nearly every dataset does.
        /// </summary>
        public static FieldProxy[] FilmsFields =>
        [
            new() { FieldName = "title", Searchable = true, Weight = 3f },
            new() { FieldName = "originalTitle", Searchable = true, Weight = 2f },
            new() { FieldName = "directors", Searchable = true, Filterable = true, Weight = 1.5f },
            new() { FieldName = "cast", Searchable = true, Weight = 1.2f },
            new() { FieldName = "description", Searchable = true, Weight = 0.8f },
            new() { FieldName = "genres", Filterable = true, Facetable = true },
            new() { FieldName = "countries", Filterable = true, Facetable = true },
            new() { FieldName = "languages", Filterable = true, Facetable = true },
            new() { FieldName = "year", Filterable = true, Facetable = true, Sortable = true },
            new() { FieldName = "durationMinutes", Filterable = true, Sortable = true },
        ];

        /// <summary>Whether the films ship with this build (the resource is embedded).</summary>
        public static bool FilmsAvailable => typeof(DemoDatasets).Assembly.GetManifestResourceInfo(FilmsResource) != null;

        /// <summary>The films unpacked into memory, about 15 MB: analysing and loading read a stream
        /// they can seek in, which a GZipStream is not.</summary>
        private static MemoryStream OpenFilms()
        {
            using var packed = typeof(DemoDatasets).Assembly.GetManifestResourceStream(FilmsResource)
                               ?? throw new InvalidOperationException("The demo films are not in this build.");
            using var gzip = new GZipStream(packed, CompressionMode.Decompress);
            var films = new MemoryStream();
            gzip.CopyTo(films);
            films.Position = 0;
            return films;
        }

        /// <summary>
        /// Creates the films demo in the team and waits until it is Ready: create, analyse, set the
        /// field roles, load, index. Returns the dataset's name. Throws when a step fails, with a
        /// message that can be shown; a dataset left half made is deleted again, so a retry starts
        /// clean.
        /// </summary>
        public async Task<string> CreateFilmsAsync(string teamId, CancellationToken ct = default)
        {
            var manager = IndxServerInternalApi.Manager;
            var name = FreeName(manager.GetTeamDataSets(teamId));
            if (DatasetCreation.TryCreate(name, teamId, logger, out name) is { } refused)
                throw new InvalidOperationException(refused);
            try
            {
                metadata.SaveKeyField(teamId, name, "id");
                metadata.Save(teamId, name, FilmsDescription);

                using var films = OpenFilms();
                if (await manager.InitFromStreamAsync(name, teamId, films) is { } initError)
                    throw new InvalidOperationException($"The demo could not be analysed: {initError}");
                ct.ThrowIfCancellationRequested();

                var engine = manager.FindSearchEngine(name, teamId) ?? throw new InvalidOperationException("The demo dataset was not found.");
                if (!engine.TrySetFieldConfiguration(FilmsFields, out var fieldsError))
                    throw new InvalidOperationException($"The demo's field roles were refused: {fieldsError}");
                if (engine.DocumentFields != null)
                    engine.Persistence?.SaveDocumentFields(engine.DocumentFields.GetSerialized());

                {
                    films.Position = 0;
                    var loading = new ProcessMonitor();
                    if (!manager.Load(name, teamId, films, loading))
                        throw new InvalidOperationException("The demo could not be loaded.");
                    await Task.Run(() => loading.WaitForCompletion(), ct);
                    if (!loading.Succeeded)
                        throw new InvalidOperationException($"The demo could not be loaded: {loading.ErrorMessage}");
                }

                var indexing = new ProcessMonitor();
                using (manager.TrackMonitor(name, teamId, indexing))
                {
                    engine.Index(monitor: indexing);
                    await Task.Run(() => indexing.WaitForCompletion(), ct);
                }
                if (!indexing.Succeeded || engine.Status.SystemState != SystemState.Ready)
                    throw new InvalidOperationException($"The demo could not be indexed: {indexing.ErrorMessage ?? engine.Status.SystemState.ToString()}");
                logger.LogInformation("Demo dataset {DataSet} created for team {TeamId}: {Count} films", name, teamId, engine.Status.DocumentCount);
                return name;
            }
            catch
            {
                manager.DeleteDataSet(name, teamId);
                metadata.Delete(teamId, name);
                throw;
            }
        }

        /// <summary>"films", or "films-2", "films-3" and on, the first the team does not have.</summary>
        internal static string FreeName(IEnumerable<string> existing)
        {
            var taken = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
            if (!taken.Contains(FilmsName)) return FilmsName;
            for (var n = 2; ; n++)
                if (!taken.Contains($"{FilmsName}-{n}")) return $"{FilmsName}-{n}";
        }
    }
}
