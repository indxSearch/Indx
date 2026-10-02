namespace IndxServer.Models
{
    /// <summary>
    /// The select event: the user chose a result. QueryId is the value the search response
    /// returned in its <c>Indx-Query-Id</c> header; it is optional and opaque — an unknown or
    /// expired id still counts on the document, it just finds no search to join. Position is
    /// 1-based in the result list. Subject is the optional general identity: a user, a session
    /// or a customer-defined segment. Count is false for a select that is not a visitor's (a
    /// probe, a test); a select on a search that was not counted is not counted either, whatever
    /// it says. See Notes/statistics-design.md.
    /// </summary>
    public record SelectEventRequest(string? QueryId, long DocumentKey, int Position, string? Subject,
        bool? Count = null);

    /// <summary>
    /// The convert event: whatever the customer considers valuable (order, add-to-cart, …).
    /// Type is the customer's own name for it. Value/Currency/Quantity are optional commerce
    /// magnitudes. QueryId is optional — a conversion may happen long after the search or from
    /// another channel, and still counts on the document. Count as for
    /// <see cref="SelectEventRequest"/>.
    /// </summary>
    public record ConvertEventRequest(string? QueryId, long DocumentKey, string Type,
        double? Value, string? Currency, long? Quantity, string? Subject, bool? Count = null);
}
