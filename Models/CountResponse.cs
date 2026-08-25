namespace IndxCloudApi.Models
{
    /// <summary>
    /// Envelope for count-valued responses on the modern API surface
    /// (<c>{"count": n}</c>). The legacy route aliases unwrap it back to the
    /// naked number those routes have always returned.
    /// </summary>
    public sealed record CountResponse(int Count);
}
