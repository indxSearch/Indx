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
        public static readonly RenderFragment<int> Delete = Of<Icons.Stop>();
        public static readonly RenderFragment<int> Boost = Of<Icons.Boost>();
        public static readonly RenderFragment<int> Synonym = Of<Icons.Synonym>();
        public static readonly RenderFragment<int> Save = Of<Icons.Save>();
        public static readonly RenderFragment<int> WeightLow = Of<Icons.WeightLow>();
        public static readonly RenderFragment<int> WeightMedium = Of<Icons.WeightMedium>();
        public static readonly RenderFragment<int> WeightHigh = Of<Icons.WeightHigh>();
        public static readonly RenderFragment<int> Speedometer = Of<Icons.Speedometer>();
        public static readonly RenderFragment<int> Status = Of<Icons.Status>();
        public static readonly RenderFragment<int> Download = Of<Icons.Download>();
        public static readonly RenderFragment<int> DynamicJson = Of<Icons.DynamicJsonField>();
        public static readonly RenderFragment<int> Search = Of<Icons.Search>();
        public static readonly RenderFragment<int> Sliders = Of<Icons.SlidersHorizontal>();
        public static readonly RenderFragment<int> Eye = Of<Icons.Eye>();
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
