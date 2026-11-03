using System.Reflection;
using Indx.Api;
using Indx.Systm.Blazor;
using Indx.Systm.Blazor.Icons;
using Microsoft.AspNetCore.Components;

namespace IndxServer.Shared
{
    /// <summary>The admin area's sections and version, for the side column (ConsoleSideMenu reads
    /// Items and VersionLine). It was a tab bar component until the column took the sections.</summary>
    public static class AdminNav
    {
        /// <summary>"Indx 2.0.0 · Core 5.0.5": the server and the library, for the column's note.</summary>
        public static string VersionLine => $"Indx {_apiVersion} · Core {_indxVersion}";

        // The product version (5.0.1), not the assembly version: IndxSearchLib pins AssemblyVersion
        // at 5.0.0.0 so an application built on 5.0.0 loads every 5.0.x unchanged, which made this
        // read "Core 5.0.0" on every patch release. The informational version carries the release,
        // with "+<commit>" after it when the build adds one.
        private static readonly string _indxVersion =
            Assembly.GetAssembly(typeof(SearchEngine)) is { } indx
                ? indx.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
                  ?? indx.GetName().Version?.ToString(3) ?? ""
                : "";

        private static readonly string _apiVersion = GetApiVersion();

        private static string GetApiVersion()
        {
            var version = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
            var plusIndex = version.IndexOf('+');
            return plusIndex > 0 ? version[..plusIndex] : version;
        }

        private static RenderFragment<int> Of<T>() where T : IComponent => size => b =>
        {
            b.OpenComponent<T>(0);
            b.AddAttribute(1, "Size", size);
            b.AddAttribute(2, "Color", "currentColor");
            b.CloseComponent();
        };

        /// <summary>The admin sections, in order; the side column's menu lists them.</summary>
        public static readonly Tabs.TabItem[] Items =
        [
            new("Users", "Users", Of<Users>(), Href: "/admin/users"),
            new("Teams", "Teams", Of<UserGroup>(), Href: "/admin/teams"),
            new("Datasets", "Datasets", Of<Database>(), Href: "/admin/datasets"),
            new("Monitor", "Monitor", Of<Indx.Systm.Blazor.Icons.Server>(), Href: "/admin/monitor"),
            new("Settings", "Settings", Of<SlidersHorizontal>(), Href: "/admin/settings"),
            new("License", "License", Of<DocumentOrFile>(), Href: "/admin/license"),
            new("Observr", "Observr", Of<Indx.Systm.Blazor.Icons.Observr>(), Href: "/admin/observr"),
            new("Email preview", "EmailPreview", Of<Bell>(), Href: "/admin/email-preview"),
        ];
    }
}
