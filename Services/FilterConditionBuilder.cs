using Indx.Api;

namespace IndxServer.Services
{
    /// <summary>
    /// Builds a single engine <see cref="Filter"/> from a declarative condition (exact value OR
    /// numeric range). Shared by the boost-rules engine and the MCP search tool so both express
    /// filters the same way. Returns null when the field isn't filterable, the condition is empty,
    /// or the engine refuses it; the <c>error</c> out-parameter then says why in the engine's words.
    /// <para>A value on a numeric field is built as a range with equal limits. The engine refuses
    /// a value filter there - it compares text, so 129 misses 129.0 - and the range is the right
    /// filter; a saved boost rule or an agent's condition should not have to know that.</para>
    /// </summary>
    public static class FilterConditionBuilder
    {
        /// <summary>
        /// Range form when <paramref name="min"/> or <paramref name="max"/> is set; otherwise an
        /// exact value match. An open bound uses double.MinValue / double.MaxValue.
        /// </summary>
        public static Filter? Build(ISearchEngine engine, string field, string? value, double? min, double? max)
            => Build(engine, field, value, min, max, out _);

        /// <inheritdoc cref="Build(ISearchEngine, string, string?, double?, double?)"/>
        public static Filter? Build(ISearchEngine engine, string field, string? value, double? min, double? max, out string? error)
        {
            error = null;
            if (min.HasValue || max.HasValue)
                return engine.CreateRangeFilter(field, min ?? double.MinValue, max ?? double.MaxValue, out error);
            if (string.IsNullOrEmpty(value))
                return null;
            if (engine.GetField(field) is { Type: System.Text.Json.JsonValueKind.Number }
                && double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number))
                return engine.CreateRangeFilter(field, number, number, out error);
            return engine.CreateValueFilter(field, value, isCaseSensitive: false, out error);
        }
    }
}
