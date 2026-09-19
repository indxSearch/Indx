using Indx.Api;
using Microsoft.AspNetCore.Components;

namespace IndxServer.Components.Datasets
{
    /// <summary>
    /// How a dataset state looks as a chip: the one place that decides it. The dataset header, the
    /// Status tab, the team's dataset list and the admin datasets page each used to carry a copy,
    /// and the copies drifted: an errored dataset was red on three pages and yellow on the fourth,
    /// Loading was light blue in one place and yellow in the others.
    ///
    /// The colours mean one thing each:
    ///   teal        Ready, serving searches
    ///   yellow      something is working on it (loading, loaded and waiting, indexing, waking, rebuilding)
    ///   light blue  asleep (hibernated, or on its way there)
    ///   red         Error, needs someone
    ///   grey        nothing in it yet
    /// Light blue is reserved for hibernation on purpose. Used for Loading as well, the list could
    /// not tell a dataset going to sleep from one waking up.
    /// </summary>
    public readonly record struct DatasetStateChip(string Color, string TextColor, RenderFragment<int>? Icon)
    {
        private const string Dark = "#080809";

        public static DatasetStateChip For(SystemState state) => state switch
        {
            SystemState.Ready => new("var(--CTeal)", Dark, DatasetIcons.ForState(state)),
            SystemState.Loading or SystemState.Loaded or SystemState.Indexing => Working(state),
            SystemState.Hibernated => Hibernated,
            SystemState.Error => new("var(--CSignal)", "var(--lv0)", DatasetIcons.ForState(state)),
            _ => Empty,
        };

        /// <summary>In progress under a label that is not a SystemState ("Waking", "Rebuilding").
        /// Pass the state whose icon fits; Loading by default.</summary>
        public static DatasetStateChip Working(SystemState iconOf = SystemState.Loading) =>
            new("var(--CWarning)", Dark, DatasetIcons.ForState(iconOf) ?? DatasetIcons.ForState(SystemState.Loading));

        /// <summary>Hibernated and "Hibernating". The console calls a Created engine with records on
        /// disk hibernated, so callers ask for this by name rather than through a SystemState.</summary>
        public static DatasetStateChip Hibernated => new("var(--CLightBlue)", Dark, DatasetIcons.Hibernate);

        public static DatasetStateChip Empty => new("var(--lv3)", "var(--lv5)", DatasetIcons.ForState(SystemState.Created));
    }
}
