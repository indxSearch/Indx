namespace IndxServer.Monitor
{
    internal enum MonitorMode
    {
        /// <summary>Disabled: render nothing, collect nothing.</summary>
        Off,
        /// <summary>A terminal is attached. Stage one still renders the text block here; the
        /// Terminal.Gui renderer replaces it without changing anything else.</summary>
        Interactive,
        /// <summary>stdout is redirected — Azure log stream, <c>docker logs</c>, systemd. An
        /// append-only block, never cursor addressing: those pipes have no TTY, render ANSI as
        /// garbage, and bill and throttle by volume.</summary>
        Piped,
    }

    /// <summary>
    /// How the monitor was configured and what the environment can actually display.
    ///
    /// <para>Interactive is on by default whenever a terminal is attached: if someone is sitting
    /// at one, show them the monitor. Piped is <b>opt-in</b> — adding a status block to every
    /// self-hoster's <c>docker logs</c> uninvited is not a friendly default, and on Azure log
    /// volume is billed. Set <c>Indx:Monitor:Enabled=true</c> there.</para>
    ///
    /// <para>Either way <c>--no-monitor</c> or <c>Indx:Monitor:Enabled=false</c> turns it off, so
    /// an operator who starts the server by hand on a production box and does not want the console
    /// taken over has a way to say so.</para>
    /// </summary>
    internal sealed record MonitorOptions(MonitorMode Mode, TimeSpan PollInterval, TimeSpan StatusInterval)
    {
        internal const string EnabledKey = "Indx:Monitor:Enabled";
        internal const string StatusSecondsKey = "Indx:Monitor:StatusSeconds";

        /// <summary>The collector always ticks at this rate: it is what gives transition events
        /// their granularity, and it is cheap (dictionary reads plus one COUNT(*) per dataset).
        /// How often anything is *printed* is <see cref="StatusInterval"/>, which is a renderer
        /// decision, not a collection one.</summary>
        private static readonly TimeSpan DefaultPoll = TimeSpan.FromSeconds(1);

        internal static MonitorOptions Resolve(IConfiguration configuration, string[] args)
        {
            bool hasTerminal = !Console.IsOutputRedirected && !Console.IsInputRedirected;

            bool? configured = configuration.GetValue<bool?>(EnabledKey);
            if (args.Contains("--no-monitor")) configured = false;
            else if (args.Contains("--monitor")) configured = true;

            bool enabled = configured ?? hasTerminal;

            var mode = !enabled ? MonitorMode.Off
                : hasTerminal ? MonitorMode.Interactive
                : MonitorMode.Piped;

            int statusSeconds = configuration.GetValue<int?>(StatusSecondsKey)
                                ?? (mode == MonitorMode.Interactive ? 10 : 30);

            return new MonitorOptions(mode, DefaultPoll, TimeSpan.FromSeconds(Math.Max(1, statusSeconds)));
        }
    }
}
