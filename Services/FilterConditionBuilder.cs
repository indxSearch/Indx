using Indx.Api;

namespace IndxCloudApi.Services
{
    /// <summary>
    /// Builds a single engine <see cref="Filter"/> from a declarative condition (exact value OR
    /// numeric range). Shared by the boost-rules engine and the MCP search tool so both express
    /// filters the same way. Returns null when the field isn't filterable / the condition is empty.
    /// </summary>
    public static class FilterConditionBuilder
    {
        /// <summary>
        /// Range form when <paramref name="min"/> or <paramref name="max"/> is set; otherwise an
        /// exact value match. An open bound uses double.MinValue / double.MaxValue.
        /// </summary>
        public static Filter? Build(ISearchEngine engine, string field, string? value, double? min, double? max)
        {
            if (min.HasValue || max.HasValue)
                return engine.CreateRangeFilter(field, min ?? double.MinValue, max ?? double.MaxValue, out _);
            if (!string.IsNullOrEmpty(value))
                return engine.CreateValueFilter(field, value, isCaseSensitive: false, out _);
            return null;
        }
    }
}
