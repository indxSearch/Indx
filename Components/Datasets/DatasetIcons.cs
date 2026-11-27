using Microsoft.AspNetCore.Components;
using Icons = Indx.Systm.Blazor.Icons;

namespace IndxServer.Components.Datasets
{
    /// <summary>
    /// The pixl icons the dataset console passes to systm <c>Button</c>/<c>Chip</c> as
    /// <c>RenderFragment&lt;int&gt;</c> (the int is the size the control wants). Static so
    /// every tab component shares one set instead of each redeclaring them.
    /// </summary>
    public static class DatasetIcons
    {
        private static RenderFragment<int> Of<T>(string? color = null) where T : IComponent => size => builder =>
        {
            builder.OpenComponent<T>(0);
            builder.AddAttribute(1, "Size", size);
            if (color != null) builder.AddAttribute(2, "Color", color);
            builder.CloseComponent();
        };

        public static readonly RenderFragment<int> Agent = Of<Icons.AiAgent>();
        public static readonly RenderFragment<int> PanelAdd = Of<Icons.PanelAdd>();
        public static readonly RenderFragment<int> Fields = Of<Icons.Field>();
        /// <summary>Cold storage beside a record.</summary>
        public static readonly RenderFragment<int> Cold = Of<Icons.Frost>();
        /// <summary>Field configuration, the page and the way to it (Nov 2026).</summary>
        public static readonly RenderFragment<int> FieldConfiguration = Of<Icons.FieldConfiguration>();
        /// <summary>Editing something in place: a field's profile (Nov 2026).</summary>
        public static readonly RenderFragment<int> Edit = Of<Icons.Compose>();
        /// <summary>The profile manager (Nov 2026).</summary>
        public static readonly RenderFragment<int> Profiles = Of<Icons.FieldProfileTemplate>();
        /// <summary>A field profile's typos at a glance (Nov 2026): ld0 a whole line, ld1 one gap, ld2 two, ld3 three.</summary>
        public static RenderFragment<int> ForTolerance(Indx.Api.Tolerance t) => t switch
        {
            Indx.Api.Tolerance.Exact => Ld0,
            Indx.Api.Tolerance.TwoEdits => Ld2,
            Indx.Api.Tolerance.ThreeEdits => Ld3,
            _ => Ld1,
        };
        /// <summary>A rerank weight that is not the default 1: lighter, heavier, or 0, which confirms no words.</summary>
        public static RenderFragment<int>? ForRerankWeight(float w) =>
            w == 0 ? Warning : w < 1 ? WeightLow : w > 1 ? WeightHigh : null;
        private static readonly RenderFragment<int> Ld0 = Of<Icons.Ld0>();
        private static readonly RenderFragment<int> Ld1 = Of<Icons.Ld1>();
        private static readonly RenderFragment<int> Ld2 = Of<Icons.Ld2>();
        private static readonly RenderFragment<int> Ld3 = Of<Icons.Ld3>();
        public static readonly RenderFragment<int> Categories = Of<Icons.NodeTree>();   // the Trees pixl once it is published
        public static readonly RenderFragment<int> NeverSelected = Of<Icons.Ghost>();
        public static readonly RenderFragment<int> NeverPurchased = Of<Icons.EmptyBox>();
        public static readonly RenderFragment<int> Maximize = Of<Icons.Maximize>();
        public static readonly RenderFragment<int> TreeView = Of<Icons.NodeTree>();
        // The Status tab's tiles.
        public static readonly RenderFragment<int> Documents = Of<Icons.DocumentOrFile>();
        public static readonly RenderFragment<int> Disk = Of<Icons.Database>();
        public static readonly RenderFragment<int> Calendar = Of<Icons.Calendar>();
        public static readonly RenderFragment<int> Clock = Of<Icons.Clock>();
        // The statistics tiles' label icons, and the monitor's.
        public static readonly RenderFragment<int> Cpu = Of<Icons.Cpu>();
        public static readonly RenderFragment<int> Ram = Of<Icons.Ram>();
        public static readonly RenderFragment<int> ClickThrough = Of<Icons.ClickButton>();
        public static readonly RenderFragment<int> ClickPosition = Of<Icons.ClickSelect>();
        public static readonly RenderFragment<int> Conversions = Of<Icons.ShoppingCart>();
        public static readonly RenderFragment<int> NotAnswered = Of<Icons.Empty>();
        public static readonly RenderFragment<int> SourcesOpened = Of<Icons.ExternalLink>();
        public static readonly RenderFragment<int> Cost = Of<Icons.Coins>();
        public static readonly RenderFragment<int> Calls = Of<Icons.Pulse>();
        public static readonly RenderFragment<int> ListView = Of<Icons.HierarchyList>();
        public static readonly RenderFragment<int> Minimize = Of<Icons.Minimize>();
        public static readonly RenderFragment<int> Delete = Of<Icons.Stop>();
        public static readonly RenderFragment<int> Boost = Of<Icons.Boost>();
        public static readonly RenderFragment<int> Bury = Of<Icons.BuryShovel>();
        public static readonly RenderFragment<int> Statistics = Of<Icons.Graph>();
        public static readonly RenderFragment<int> Synonym = Of<Icons.Synonym>();
        public static readonly RenderFragment<int> Webhooks = Of<Icons.Webhooks>();
        /// <summary>A webhook plugin: a source Indx connects to without code (Sanity, Contentful).</summary>
        public static readonly RenderFragment<int> Plugin = Of<Icons.PuzzlePiece>();
        public static readonly RenderFragment<int> QueryParameters = Of<Icons.Coverage>();
        public static readonly RenderFragment<int> SearchOrAsk = Of<Icons.SearchOrAsk>();
        public static readonly RenderFragment<int> Observr = Of<Icons.Observr>();
        /// <summary>Hands a setting back to its default.</summary>
        public static readonly RenderFragment<int> Restore = Of<Icons.Refresh>();
        public static readonly RenderFragment<int> ChevronRight = Of<Icons.ChevronRight>();
        /// <summary>"Open this", beside a name that is not itself a link.</summary>
        public static readonly RenderFragment<int> Open = Of<Icons.ArrowUpRightAlt>();
        public static readonly RenderFragment<int> ChevronDown = Of<Icons.ChevronDown>();
        public static readonly RenderFragment<int> Experimental = Of<Icons.LabExperiment>();
        public static readonly RenderFragment<int> Save = Of<Icons.Save>();
        public static readonly RenderFragment<int> WeightLow = Of<Icons.WeightLow>();
        public static readonly RenderFragment<int> WeightMedium = Of<Icons.WeightMedium>();
        public static readonly RenderFragment<int> WeightHigh = Of<Icons.WeightHigh>();
        public static readonly RenderFragment<int> Speedometer = Of<Icons.Speedometer>();

        // The Statistics tab's sections.
        public static readonly RenderFragment<int> TopQueries = Of<Icons.SearchQuery>();
        public static readonly RenderFragment<int> FuzzySearch = Of<Icons.FuzzySearch>();
        /// <summary>Searches no record held every word of: Statistics' Partial matches (6 Nov 2026).</summary>
        public static readonly RenderFragment<int> PartialSearch = Of<Icons.PartialSearch>();
        public static readonly RenderFragment<int> TopDocuments = Of<Icons.SortAscending>();
        public static readonly RenderFragment<int> Browsing = Of<Icons.InternetBrowser>();
        // Statistics' Journeys: the section, and a value picked from a facet's own search as a step of one.
        public static readonly RenderFragment<int> Journeys = Of<Icons.Milestone>();
        public static readonly RenderFragment<int> FacetPick = Of<Icons.SearchAsYouType>();
        public static readonly RenderFragment<int> Changes = Of<Icons.Update>();

        // The security strip's actions: a release on GitHub (outside the console), how to update, hide it.
        public static readonly RenderFragment<int> ExternalLink = Of<Icons.ExternalLink>();
        public static readonly RenderFragment<int> HowTo = Of<Icons.Question>();
        public static readonly RenderFragment<int> Hide = Of<Icons.Ghost>();
        public static readonly RenderFragment<int> Mail = Of<Icons.Mail>();
        public static readonly RenderFragment<int> Bell = Of<Icons.Bell>();
        public static readonly RenderFragment<int> User = Of<Icons.User>();
        public static readonly RenderFragment<int> Team = Of<Icons.UserGroup>();
        public static readonly RenderFragment<int> Key = Of<Icons.Key>();
        /// <summary>Records' Matched column, beside Key: which fields a search found a record by.</summary>
        public static readonly RenderFragment<int> Matched = Of<Icons.SearchQuery>();
        public static readonly RenderFragment<int> AskedAgain = Of<Icons.Loop>();
        public static readonly RenderFragment<int> FuzzySelected = Of<Icons.ClickSelect>();
        // The Search or ask views: the pictures, the questions asked, the offers shown.
        public static readonly RenderFragment<int> AskOverview = Of<Icons.Analytics>();
        public static readonly RenderFragment<int> AskQuestions = Of<Icons.Question>();
        public static readonly RenderFragment<int> AskNeeds = Of<Icons.Layers>();
        public static readonly RenderFragment<int> AskOffers = Of<Icons.LightBulb>();
        /// <summary>Full offers whose list did not hold what was asked (the judge's found), the list's icon under While asking.</summary>
        public static readonly RenderFragment<int> AskListMissed = Of<Icons.Results>();
        public static readonly RenderFragment<int> AskProactive = Of<Icons.Spark>();
        public static readonly RenderFragment<int> FuzzyAll = Of<Icons.SearchAsYouType>();
        public static readonly RenderFragment<int> ShowMore = Of<Icons.Plus>();
        /// <summary>Erasing data, as opposed to <see cref="Delete"/>, which removes the dataset.</summary>
        public static readonly RenderFragment<int> Erase = Of<Icons.Delete>();
        public static readonly RenderFragment<int> Yes = Of<Icons.Check>();
        public static readonly RenderFragment<int> No = Of<Icons.XOrError>();
        public static readonly RenderFragment<int> OpenFinding = Of<Icons.GitIssue>();
        public static readonly RenderFragment<int> Archive = Of<Icons.Folder>();

        /// <summary>The speedometer as a running spinner — the benchmark button swaps to this
        /// while a run is in progress, so the icon itself says "measuring".</summary>
        public static readonly RenderFragment<int> SpeedometerRunning = size => builder =>
        {
            builder.OpenComponent<Indx.Systm.Blazor.Spinner>(0);
            builder.AddAttribute(1, "Name", "speedometer");
            builder.AddAttribute(2, "Size", size);
            builder.AddAttribute(3, "AriaLabel", "Running benchmark");
            builder.CloseComponent();
        };
        public static readonly RenderFragment<int> Status = Of<Icons.Status>();
        public static readonly RenderFragment<int> Download = Of<Icons.Download>();
        public static readonly RenderFragment<int> DynamicJson = Of<Icons.DynamicJsonField>();
        public static readonly RenderFragment<int> Search = Of<Icons.Search>();
        public static readonly RenderFragment<int> Sliders = Of<Icons.SlidersHorizontal>();
        // The Records section (Nov 2026): the section, a filter, the columns.
        public static readonly RenderFragment<int> Records = Of<Icons.TableIcon>();
        public static readonly RenderFragment<int> Filter = Of<Icons.Filter>();
        public static readonly RenderFragment<int> Json = Of<Icons.Json>();
        public static readonly RenderFragment<int> SortAscending = Of<Icons.SortAscending>();
        public static readonly RenderFragment<int> SortDescending = Of<Icons.SortDescending>();
        // A row that opens in place, a searched field a record lacks, a text the index cut.
        public static readonly RenderFragment<int> RowClosed = Of<Icons.ChevronRight>();
        public static readonly RenderFragment<int> RowOpen = Of<Icons.ChevronDown>();
        public static readonly RenderFragment<int> Lacking = Of<Icons.Warning>();
        public static readonly RenderFragment<int> Cut = Of<Icons.Scissors>();
        public static readonly RenderFragment<int> Fixed = Of<Icons.Check>();
        // Running a scan of the records, and opening its report.
        public static readonly RenderFragment<int> Scan = Of<Icons.TrendingSearch>();
        public static readonly RenderFragment<int> Report = Of<Icons.DocumentFields>();
        public static readonly RenderFragment<int> Eye = Of<Icons.Eye>();
        public static readonly RenderFragment<int> Lock = Of<Icons.Lock>();
        public static readonly RenderFragment<int> Tag = Of<Icons.Tag>();
        public static readonly RenderFragment<int> Flag = Of<Icons.Flag>();
        public static readonly RenderFragment<int> Trolley = Of<Icons.Trolley>();
        public static readonly RenderFragment<int> HourGlass = Of<Icons.HourGlass>();
        public static readonly RenderFragment<int> Warning = Of<Icons.Warning>();
        public static readonly RenderFragment<int> Hibernate = Of<Icons.Hibernate>();
        public static readonly RenderFragment<int> Empty = Of<Icons.Empty>();
        public static readonly RenderFragment<int> ShadowIndexing = Of<Icons.ShadowIndexing>();
        public static readonly RenderFragment<int> StringType = Of<Icons.StringType>("var(--lv7)");
        public static readonly RenderFragment<int> NumberType = Of<Icons.NumberType>("var(--lv7)");
        public static readonly RenderFragment<int> BoolType = Of<Icons.BoolType>("var(--lv7)");
        public static readonly RenderFragment<int> ArrayType = Of<Icons.ArrayType>("var(--lv7)");
        public static readonly RenderFragment<int> NestedObjectType = Of<Icons.NestedObject>("var(--lv7)");
        /// <summary>The badge icon for a field Analyze never read a value for, so it has no type.</summary>
        public static readonly RenderFragment<int> NoValueType = Of<Icons.Stop>("var(--lv7)");

        public static RenderFragment<int>? ForFieldType(string? type, bool? isArray) =>
            isArray == true ? ArrayType : type switch
            {
                "String" => StringType,
                "Number" => NumberType,
                "Boolean" => BoolType,
                _ => null,
            };

        /// <summary>Ready=Flag, Loading=Trolley, Indexing=Hour glass, Error=Warning, Created=Empty.
        /// Hibernated uses its own chip with the Hibernate icon.</summary>
        public static RenderFragment<int>? ForState(Indx.Api.SystemState state) => state switch
        {
            Indx.Api.SystemState.Ready => Flag,
            Indx.Api.SystemState.Loading => Trolley,
            Indx.Api.SystemState.Indexing => HourGlass,
            Indx.Api.SystemState.Error => Warning,
            Indx.Api.SystemState.Created => Empty,
            _ => null,
        };
    }
}
