using Indx.Http;

namespace IndxServer.Models
{
    /// <summary>
    /// A dataset's query parameters: the values a search takes when its request leaves them out.
    /// The same shape, names and meaning as the coverage part of a search body, so a value can be
    /// copied between the two. Null means "not set here", and the engine default applies.
    /// Resolution, field by field: the request, then this, then the engine default
    /// (<see cref="Services.QueryParameterResolution"/>).
    /// </summary>
    public sealed class DatasetQueryParameters
    {
        /// <summary>See <see cref="QueryProxy.CoverageDepth"/>.</summary>
        public int? CoverageDepth { get; set; }

        /// <summary>See <see cref="QueryProxy.CoverageSetup"/>.</summary>
        public CoverageSetupProxy? CoverageSetup { get; set; }
    }

    /// <summary>GET …/query-parameters: what the dataset sets, and what a search that sends no
    /// coverage values runs with.</summary>
    /// <param name="Parameters">The values the dataset sets; the rest are null.</param>
    /// <param name="Effective">Every value filled in: the dataset's where set, else the engine default.</param>
    public sealed record QueryParametersResponse(DatasetQueryParameters Parameters, DatasetQueryParameters Effective);
}
