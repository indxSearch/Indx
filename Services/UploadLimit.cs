using System.Globalization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace IndxServer.Services
{
    /// <summary>
    /// The most one request body may hold, a whole dataset in analyze, load or replace, and the most
    /// the console uploads in one file: <c>Indx:MaxUploadBytes</c>, 2,000,000,000 by default (6 Nov
    /// 2026; a constant until then). Raise it on a server with the memory and disk for larger
    /// datasets; the default keeps a shared or small host from being filled by one request. Under
    /// IIS (Windows App Service) the request filtering in front allows at most 4 GB
    /// (<see cref="IisMaximum"/>), whatever is set here, and Azure's front end cuts a request that
    /// is not answered within about 230 seconds.
    /// </summary>
    public sealed class UploadLimit(long bytes)
    {
        public const string Setting = "Indx:MaxUploadBytes";
        public const long Default = 2_000_000_000;
        /// <summary>IIS's maxAllowedContentLength is a 32-bit unsigned number.</summary>
        public const long IisMaximum = uint.MaxValue;

        public long Bytes { get; } = bytes;

        /// <summary>The setting, or the default where it is missing, not a number, or not positive.</summary>
        public static UploadLimit From(IConfiguration configuration) =>
            new(long.TryParse(configuration[Setting], NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) && b > 0 ? b : Default);

        /// <summary>Why a file of this size is not taken, or null when it is.</summary>
        public string? Refusal(long size) => size <= Bytes ? null
            : $"The file is {Gb(size)}; this server takes up to {Gb(Bytes)} in one upload. An administrator can raise {Setting}.";

        /// <summary>In decimal gigabytes, as the default's 2 GB is.</summary>
        public static string Gb(long bytes) => (bytes / 1e9).ToString("0.##", CultureInfo.InvariantCulture) + " GB";
    }

    /// <summary>
    /// <see cref="RequestSizeLimitAttribute"/> at <see cref="UploadLimit"/>: the routes that take a
    /// whole dataset in one body. Read from the setting, which an attribute's constant cannot be.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
    public sealed class UploadSizeLimitAttribute : Attribute, IFilterFactory, IOrderedFilter
    {
        // RequestSizeLimitAttribute's order, so it runs where that one did.
        public int Order { get; set; } = 900;
        public bool IsReusable => true;

        public IFilterMetadata CreateInstance(IServiceProvider services) =>
            new Policy(services.GetRequiredService<UploadLimit>().Bytes);

        private sealed class Policy(long bytes) : IAuthorizationFilter, IRequestSizePolicy
        {
            public void OnAuthorization(AuthorizationFilterContext context)
            {
                // The closest size policy decides, as MVC's own does.
                if (context.FindEffectivePolicy<IRequestSizePolicy>() is { } effective && !ReferenceEquals(effective, this)) return;
                if (context.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
                    feature.MaxRequestBodySize = bytes;
            }
        }
    }
}
