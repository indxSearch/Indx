namespace IndxServer.Services.Webhooks
{
    public static class WebhookServices
    {
        /// <summary>The webhooks' services and their sources, in the order the console offers them.
        /// The worker is a hosted service only where the host adds it (Program does; tests need not).</summary>
        public static IServiceCollection AddIndxWebhooks(this IServiceCollection services)
        {
            services.AddSingleton<WebhookStore>();
            services.AddHttpClient(SanityClient.HttpClientName, c => c.Timeout = TimeSpan.FromMinutes(5));
            services.AddHttpClient(ContentfulClient.HttpClientName, c => c.Timeout = TimeSpan.FromMinutes(5));
            services.AddSingleton<SanityClient>();
            services.AddSingleton<ContentfulClient>();
            services.AddSingleton<IWebhookSource, IndxWebhookSource>();
            services.AddSingleton<IWebhookSource, SanityWebhookSource>();
            services.AddSingleton<IWebhookSource, ContentfulWebhookSource>();
            services.AddSingleton<WebhookSourceRegistry>();
            services.AddSingleton<WebhookApplier>();
            services.AddSingleton<WebhookWorker>();
            services.AddSingleton<WebhookAdmin>();
            return services;
        }
    }
}
