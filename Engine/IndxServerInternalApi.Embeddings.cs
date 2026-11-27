using Indx.Api;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Linq;

namespace IndxServer.Engine
{
    /// <summary>
    /// The vector graphs of a sleeping dataset, kept in a file.
    ///
    /// A dataset that sleeps keeps its documents in the store and nothing in memory, and waking
    /// it is a load from the store and an index build. For a dataset with a vector field the
    /// slow part of that load is building the search graph: nine seconds for 43 000 vectors of
    /// 1 536 numbers, four minutes for 1.2 million of 100. The graph a dataset had when it went
    /// to sleep is as good as the one a wake would build, so it is written to a file on the way
    /// down (<see cref="SearchEngine.SaveEmbeddings"/>) and read on the way up
    /// (<see cref="SearchEngine.LoadEmbeddings"/>). On WANDS the load went from 41 to 25 seconds.
    ///
    /// The file is a cache and nothing else. The store is the truth: the engine brings a graph
    /// read from a file in line with the documents it then loads, and builds from scratch when
    /// the two are too far apart or the file will not read. So a file that is missing, old, or
    /// from before a replace costs time and never correctness, and no failure in here fails the
    /// sleep or the wake it is part of.
    ///
    /// One file per dataset, in <c>embeddings</c> beside the search database: the graphs' links
    /// and the graphs' own copy of the vectors, about 3 KB per vector of 1 536 numbers.
    /// </summary>
    internal sealed partial class IndxServerInternalApi
    {
        private const string EmbeddingsFolderName = "embeddings";

        /// <summary>Where the dataset's graphs are kept. Null when the search database has no
        /// folder of its own to keep them beside.</summary>
        internal string? SavedEmbeddingsPath(string dataSetName, string teamId)
        {
            string? folder = Path.GetDirectoryName(Path.GetFullPath(SearchDbConnectionString));
            if (string.IsNullOrEmpty(folder))
                return null;
            // The dataset name is a valid file name already (FileNameValidity, at creation); the
            // team id is a GUID on a server and anything at all in a test.
            string team = new(teamId.Select(c => Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c).ToArray());
            return Path.Combine(folder, EmbeddingsFolderName, $"{team}__{dataSetName}.indxvec");
        }

        /// <summary>Writes the engine's graphs to the dataset's file, before it is unloaded. Does
        /// nothing for a dataset without a vector field.</summary>
        internal void SaveEmbeddingsForSleep(IServerSearchEngine? engine, string dataSetName, string teamId)
        {
            if (engine is not SearchEngine concrete || concrete.IsDisposed
                || concrete.Status.SystemState != SystemState.Ready || concrete.EmbeddingFields.Count == 0)
                return;
            try
            {
                string? path = SavedEmbeddingsPath(dataSetName, teamId);
                if (path == null) return;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var clock = System.Diagnostics.Stopwatch.StartNew();
                if (concrete.SaveEmbeddings(path, out string error))
                    _logger.LogInformation("{Prefix}vector graphs saved for the next wake ({Mb:F0} MB, {Ms} ms)",
                        MakeLogPrefix(teamId, dataSetName), new FileInfo(path).Length / (1024.0 * 1024.0), clock.ElapsedMilliseconds);
                else
                    _logger.LogWarning("{Prefix}vector graphs not saved, the next wake builds them: {Error}",
                        MakeLogPrefix(teamId, dataSetName), error);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                _logger.LogWarning(ex, "{Prefix}vector graphs not saved, the next wake builds them", MakeLogPrefix(teamId, dataSetName));
            }
        }

        /// <summary>Hands the dataset's saved graphs, when there are any, to the engine for the
        /// load from the store that follows. Called just before every such load.</summary>
        internal void UseSavedEmbeddings(IServerSearchEngine? engine, string dataSetName, string teamId)
        {
            if (engine is not SearchEngine concrete)
                return;
            try
            {
                string? path = SavedEmbeddingsPath(dataSetName, teamId);
                if (path == null || !File.Exists(path))
                    return;
                if (concrete.LoadEmbeddings(path, out string error))
                    _logger.LogInformation("{Prefix}vector graphs read from the file saved at the last sleep",
                        MakeLogPrefix(teamId, dataSetName));
                else
                    _logger.LogWarning("{Prefix}saved vector graphs not used, they are built instead: {Error}",
                        MakeLogPrefix(teamId, dataSetName), error);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                _logger.LogWarning(ex, "{Prefix}saved vector graphs not used, they are built instead", MakeLogPrefix(teamId, dataSetName));
            }
        }

        /// <summary>Removes the dataset's file: it was deleted, or its documents were replaced.</summary>
        internal void DeleteSavedEmbeddings(string dataSetName, string teamId)
        {
            try
            {
                string? path = SavedEmbeddingsPath(dataSetName, teamId);
                if (path != null && File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                _logger.LogWarning(ex, "{Prefix}saved vector graphs could not be removed", MakeLogPrefix(teamId, dataSetName));
            }
        }

        /// <summary>Moves the file with its dataset, on a rename or a transfer to another team.</summary>
        private void MoveSavedEmbeddings(string dataSetName, string teamId, string newDataSetName, string newTeamId)
        {
            try
            {
                string? from = SavedEmbeddingsPath(dataSetName, teamId);
                string? to = SavedEmbeddingsPath(newDataSetName, newTeamId);
                if (from == null || to == null || !File.Exists(from))
                    return;
                File.Move(from, to, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                _logger.LogWarning(ex, "{Prefix}saved vector graphs could not follow the dataset; they are built at its next wake",
                    MakeLogPrefix(teamId, dataSetName));
                DeleteSavedEmbeddings(dataSetName, teamId);
            }
        }
    }
}
