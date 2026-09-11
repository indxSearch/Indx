using Indx.Api;

using IndxCloudApi.Data;
using IndxCloudApi.Models;
using IndxCloudApi.Services;

namespace IndxCloudApi.Components.Datasets
{
    /// <summary>
    /// Everything the dataset console knows about one dataset of the active team, shared by
    /// <see cref="DatasetPanel"/> and its tab components through a cascading value. One
    /// instance per (team, dataset); the panel creates it, the tabs mutate it and call
    /// <see cref="NotifyChanged"/> so the panel re-renders the parts it owns (status table,
    /// tab nav). Replaces the name-keyed dictionaries the single-page console used to hold.
    /// </summary>
    public sealed class DatasetContext : IDisposable
    {
        internal DatasetContext(IDatasetEngines engines, string name, string teamId, string? role, IReadOnlyList<(Team Team, string Role)> myTeams)
        {
            Engines = engines;
            Name = name;
            TeamId = teamId;
            Role = role;
            MyTeams = myTeams;
        }

        /// <summary>The engine registry, injectable so tabs are testable without a live engine.</summary>
        internal IDatasetEngines Engines { get; }

        // ── Identity ──────────────────────────────────────────────────────────
        public string Name { get; }
        /// <summary>The engine registry's owner key: the team id.</summary>
        public string TeamId { get; }
        public string? Role { get; }
        public bool IsAdmin => TeamRoles.CanAdmin(Role);
        public bool IsEditor => TeamRoles.CanWrite(Role);
        public IReadOnlyList<(Team Team, string Role)> MyTeams { get; set; }

        // ── Engine view ───────────────────────────────────────────────────────
        public SystemStatus? Status { get; set; }
        internal IndxCloudInternalApi.KeepAliveInfo? KeepAlive { get; set; }
        /// <summary>True once <see cref="RefreshStatus"/> has succeeded at least once. Until then
        /// the engine state is unknown and nothing state-dependent (upload prompt, wake panel,
        /// tab nav) should render — an unknown state must not look like an empty dataset.</summary>
        public bool HasStatus => Status != null && KeepAlive != null;
        public SystemState EngineState => Status?.SystemState ?? SystemState.Created;
        /// <summary>A Created engine with records persisted on disk is hibernated (manual datasets
        /// stay this way until woken; timed/pinned wake themselves on access), not an empty dataset.</summary>
        public bool IsHibernated => EngineState == SystemState.Created && (KeepAlive?.RecordCount ?? 0) > 0;
        public bool IsTransitional => EngineState is SystemState.Loading or SystemState.Loaded or SystemState.Indexing;

        public void RefreshStatus()
        {
            try
            {
                // Status first: for a timed/pinned dataset GetState triggers the lazy reload, and the
                // keep-alive view (Ready, remaining time) must describe the engine after that.
                var status = Engines.GetState(Name, TeamId);
                var keepAlive = Engines.GetKeepAliveInfo(Name, TeamId);
                Status = status;
                KeepAlive = keepAlive;
            }
            catch (Exception ex)
            {
                // Dataset may be mid-transition; keep the last known view.
                Console.WriteLine($"[DatasetContext] status refresh failed for {Name}: {ex.Message}");
            }
        }

        public ICloudSearchEngine? FindEngine() => Engines.FindSearchEngine(Name, TeamId);

        // ── Field configuration spine ─────────────────────────────────────────
        /// <summary>The configuration as the engine currently has it (baseline for dirty checks).</summary>
        public FieldProxy[]? SavedFields { get; private set; }
        /// <summary>The user's working copy. Mutated in place by the field table; shared by
        /// reference with the search preview, the boost condition builder and the key-field row.</summary>
        public FieldProxy[]? EditedFields { get; private set; }
        public bool HasFieldConfig => SavedFields != null;

        /// <summary>Re-reads the configuration from the engine and resets the working copy.</summary>
        public void RefreshFieldsFromEngine()
        {
            var fields = FindEngine()?.GetFieldConfiguration();
            SetSavedFields(fields);
        }

        /// <summary>Loads the configuration only if none is held yet (the console's original
        /// "populate on first sight of Ready" rule).</summary>
        public void EnsureFieldsLoaded()
        {
            if (!HasFieldConfig) RefreshFieldsFromEngine();
        }

        public void SetSavedFields(FieldProxy[]? fields)
        {
            SavedFields = fields;
            EditedFields = fields?.Select(DeepCopy).ToArray();
        }

        public void ClearFields()
        {
            SavedFields = null;
            EditedFields = null;
        }

        public void CancelEdits()
        {
            EditedFields = SavedFields?.Select(DeepCopy).ToArray();
        }

        public bool IsDirty
        {
            get
            {
                var original = SavedFields;
                var edited = EditedFields;
                if (original == null || edited == null) return false;
                for (int i = 0; i < original.Length && i < edited.Length; i++)
                {
                    var o = original[i]; var e = edited[i];
                    if ((o.Searchable ?? false) != (e.Searchable ?? false)) return true;
                    if ((o.Filterable ?? false) != (e.Filterable ?? false)) return true;
                    if ((o.Facetable ?? false) != (e.Facetable ?? false)) return true;
                    if ((o.Sortable ?? false) != (e.Sortable ?? false)) return true;
                    if ((o.WordIndexing ?? false) != (e.WordIndexing ?? false)) return true;
                    if ((o.Embeddable ?? false) != (e.Embeddable ?? false)) return true;
                    if ((o.PreloadFilters ?? false) != (e.PreloadFilters ?? false)) return true;
                    if ((o.HighResolution ?? false) != (e.HighResolution ?? false)) return true;
                    if (o.Weight != e.Weight) return true;
                    if (o.BM25b != e.BM25b) return true;
                    if (o.BM25k1 != e.BM25k1) return true;
                }
                return false;
            }
        }

        /// <summary>True when at least one field is searchable — or when no configuration is
        /// held yet, in which case the engine's own defaults apply and nothing should be blocked.</summary>
        public bool HasSearchable => EditedFields == null || EditedFields.Any(f => f.Searchable == true);

        public static FieldProxy DeepCopy(FieldProxy f) => new FieldProxy
        {
            FieldName = f.FieldName,
            FieldType = f.FieldType,
            IsArray = f.IsArray,
            Searchable = f.Searchable ?? false,
            Filterable = f.Filterable ?? false,
            Facetable = f.Facetable ?? false,
            Sortable = f.Sortable ?? false,
            WordIndexing = f.WordIndexing ?? false,
            Embeddable = f.Embeddable ?? false,
            PreloadFilters = f.PreloadFilters ?? false,
            HighResolution = f.HighResolution ?? false,
            Weight = f.Weight,
            BM25b = f.BM25b,
            BM25k1 = f.BM25k1
        };

        // ── Upload hand-off ───────────────────────────────────────────────────
        /// <summary>The uploaded file after Analyze (schema scan) and before Load &amp; Index.
        /// Held open (DeleteOnClose temp file) until Load consumes it or the context is disposed.</summary>
        public FileStream? BufferedFile { get; private set; }
        public bool IsAnalyzed => BufferedFile != null;
        public string? AnalyzeWarning { get; set; }

        public void SetBufferedFile(FileStream? stream)
        {
            if (ReferenceEquals(BufferedFile, stream)) return;
            try { BufferedFile?.Dispose(); } catch { /* best effort */ }
            BufferedFile = stream;
        }

        /// <summary>Hands the buffered stream to the caller (Load) and forgets it.</summary>
        public FileStream? TakeBufferedFile()
        {
            var s = BufferedFile;
            BufferedFile = null;
            return s;
        }

        // ── Operation state (drives the status chip, tab gating and progress bars) ──
        public bool IsUploading { get; set; }
        public bool IsAnalyzing { get; set; }
        public int UploadProgress { get; set; }
        public bool IsLoadIndexing { get; set; }
        public int LoadProgress { get; set; }
        public int IndexProgress { get; set; }
        public bool IsIndexingPhase { get; set; }
        /// <summary>A field-configuration save is rebuilding the index on a shadow engine.</summary>
        public bool IsSaving { get; set; }
        public int ShadowBuildPercent { get; set; }
        /// <summary>Error from the last upload, load &amp; index, or wake — shown in the fields tab.</summary>
        public string? OperationError { get; set; }
        public bool IsBusy => IsUploading || IsLoadIndexing || IsSaving;

        // ── UI state shared between tabs ──────────────────────────────────────
        public string ActiveTab { get; set; } = "fields";
        /// <summary>One confirm token for every "delete this dataset" site across the tabs.</summary>
        public bool DeleteConfirmOpen { get; set; }

        // ── Change notification ───────────────────────────────────────────────
        /// <summary>Raised by tabs after they change shared state, so the panel (status table,
        /// tab nav) and sibling tabs can re-render.</summary>
        public event Action? Changed;
        public void NotifyChanged() => Changed?.Invoke();

        /// <summary>Raised after a Replace so the search preview drops its cached results.</summary>
        public event Action? DocumentsReplaced;
        public void NotifyDocumentsReplaced() => DocumentsReplaced?.Invoke();

        /// <summary>Raised when this dataset was deleted, transferred or hibernated — the shell
        /// reloads the team's dataset list.</summary>
        public event Func<Task>? DatasetListChanged;
        public Task NotifyDatasetListChangedAsync() => DatasetListChanged?.Invoke() ?? Task.CompletedTask;

        /// <summary>Raised with the new name after a rename — the host navigates there, since the
        /// name is in the URL and this context is bound to the old one.</summary>
        public event Func<string, Task>? Renamed;

        /// <summary>Renames the dataset. Runs off the circuit (a Ready engine reloads under the
        /// new key). Returns the message to show on refusal, null on success.</summary>
        public async Task<string?> RenameAsync(string newName)
        {
            var error = await Task.Run(() => Engines.RenameDataSet(Name, TeamId, newName));
            if (error != null) return error;
            SetBufferedFile(null);
            if (Renamed != null) await Renamed(newName.Trim());
            return null;
        }

        // ── Dataset-level operations shared by more than one tab ─────────────

        /// <summary>Deletes the dataset. Runs off the circuit (engine disposal can take
        /// seconds) and tells the shell to reload its list. Returns false on failure.</summary>
        public async Task<bool> DeleteAsync()
        {
            try
            {
                var ok = await Task.Run(() => Engines.DeleteDataSet(Name, TeamId));
                DeleteConfirmOpen = false;
                if (!ok) return false;
                SetBufferedFile(null);
                await NotifyDatasetListChangedAsync();
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error deleting dataset: {ex.Message}");
                DeleteConfirmOpen = false;
                return false;
            }
        }

        /// <summary>Unloads the engine from memory (data stays on disk). Only durable for
        /// Off (client-managed) datasets; timed/pinned reload on the next access.</summary>
        public async Task HibernateAsync()
        {
            await Task.Run(() => Engines.DisposeDataSetInstance(Name, TeamId));
            // A hibernated dataset isn't Ready, so the tab nav disappears. Land on the
            // field-config view, where the "Hibernated → Wake up" UI lives.
            ActiveTab = "fields";
            RefreshStatus();
            await NotifyDatasetListChangedAsync();
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:F1} GB";
            if (bytes >= 1_048_576) return $"{bytes / 1_048_576.0:F1} MB";
            if (bytes >= 1024) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes} B";
        }

        public void Dispose()
        {
            SetBufferedFile(null);
        }
    }
}
