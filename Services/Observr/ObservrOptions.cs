namespace IndxServer.Services.Observr
{
    /// <summary>Where Indx Observr is (<c>Indx:Observr</c>). The instance reaches out to it with
    /// its licence token; nothing is sent without one. Notes/Anders/observr-plan.md.</summary>
    public sealed class ObservrOptions
    {
        public const string Section = "Indx:Observr";

        public string Url { get; set; } = "https://observr.indx.co";

        /// <summary>The licence portal, asked whether this instance's subscription includes Observr.
        /// Only Indx issues licences, so this is the one portal; the setting exists for the tests.</summary>
        public string PortalUrl { get; set; } = "https://license.indx.co";
    }
}
