using System.Globalization;
using System.Text;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace IndxServer.Monitor;

/// <summary>
/// The instance's gauges in the terminal: CPU and memory as rows of bars, the console's BarMeter
/// in glyphs, and the search rate over the last hour as a sparkline. Three rows, the bars as
/// wide as the row leaves after the label and the figures.
///
/// <para>CPU is this process's share of all cores over the last tick; memory is resident against
/// what the runtime may use (<see cref="ProcessLine"/>). The lit bars are in the stone tone, the
/// signal red past 85 percent as in the console; the rest of the track is slate. The text parts
/// are pure functions (<see cref="Lines"/>), so a test can read them without a terminal.</para>
/// </summary>
public sealed class MeterView : View
{
    private static readonly Color Signal = new(0xFF, 0x42, 0x38), Warn = new(0xFF, 0xC1, 0x07),
                                  Stone = new(0xCF, 0xCF, 0xCF), Slate = new(0x4A, 0x4A, 0x50);
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The shares past which a meter is in the warning amber, and in the signal red: the
    /// zones the console's rings have.</summary>
    internal const double WarnFrom = 0.7, SignalFrom = 0.85;
    internal const char Lit = '▮', Dim = '▯';
    private const string Levels = "▁▂▃▄▅▆▇█";
    private const int LabelWidth = 5;

    private MonitorSnapshot? _snapshot;

    public MeterView()
    {
        Height = 3;
        CanFocus = false;
    }

    internal MonitorSnapshot? Snapshot
    {
        get => _snapshot;
        set { _snapshot = value; SetNeedsDraw(); }
    }

    /// <summary>One row: the label, the bar's lit and dim lengths, and the figures after it.</summary>
    internal readonly record struct MeterLine(string Label, int LitCells, int DimCells, string Figures, bool Signal, bool Warn = false)
    {
        /// <summary>The row as plain text, for a test or a log.</summary>
        public override string ToString() =>
            Label.PadRight(LabelWidth) + new string(Lit, LitCells) + new string(Dim, DimCells) + Figures;
    }

    /// <summary>How many of <paramref name="width"/> cells are lit for a share, the rest dim; a share
    /// above zero always lights one cell, so a quiet process is not shown as idle.</summary>
    internal static (int Lit, int Dim) Bar(double fraction, int width)
    {
        if (width <= 0) return (0, 0);
        fraction = double.IsFinite(fraction) ? Math.Clamp(fraction, 0, 1) : 0;
        int lit = (int)Math.Round(fraction * width);
        if (fraction > 0 && lit == 0) lit = 1;
        return (lit, width - lit);
    }

    /// <summary>The values as a run of eight-level bars, the latest at the right, scaled to the
    /// largest; the last <paramref name="width"/> values when there are more. Empty with none.</summary>
    internal static string Spark(IReadOnlyList<double> values, int width)
    {
        if (width <= 0 || values.Count == 0) return "";
        int from = Math.Max(0, values.Count - width);
        double max = 0;
        for (int i = from; i < values.Count; i++) if (values[i] > max) max = values[i];
        var sb = new StringBuilder(values.Count - from);
        for (int i = from; i < values.Count; i++)
        {
            int level = max <= 0 ? 0 : (int)Math.Round(values[i] / max * (Levels.Length - 1));
            sb.Append(Levels[Math.Clamp(level, 0, Levels.Length - 1)]);
        }
        return sb.ToString();
    }

    /// <summary>The three rows for a snapshot at a width: cpu, mem and rate.</summary>
    internal static MeterLine[] Lines(MonitorSnapshot s, int width)
    {
        var p = s.Process;
        double cpu = p.CpuPercent / 100.0;
        string cpuFigures = string.Create(Inv, $"  {p.CpuPercent,3:0}%  of {Environment.ProcessorCount} cores");
        var (cpuLit, cpuDim) = Bar(cpu, BarWidth(width, cpuFigures));

        long avail = Math.Max(p.AvailableMb, 1);
        double mem = p.AvailableMb > 0 ? (double)p.WorkingSetMb / avail : 0;
        string memFigures = p.AvailableMb > 0
            ? string.Create(Inv, $"  {mem * 100,3:0}%  {Mb(p.WorkingSetMb)} of {Mb(p.AvailableMb)}")
            : string.Create(Inv, $"  {Mb(p.WorkingSetMb)} resident");
        var (memLit, memDim) = Bar(mem, BarWidth(width, memFigures));

        string rateFigures = s.SearchesPerMinute >= 0.05
            ? string.Create(Inv, $"  {s.SearchesPerMinute,3:0}/min  last hour")
            : "  none/min  last hour";
        int sparkWidth = BarWidth(width, rateFigures);
        string spark = Spark(s.RateHistory, sparkWidth);

        return
        [
            new("cpu", cpuLit, cpuDim, cpuFigures, cpu >= SignalFrom, cpu >= WarnFrom),
            new("mem", memLit, memDim, memFigures, mem >= SignalFrom, mem >= WarnFrom),
            // The sparkline is its own glyphs: carried in Figures, no bar cells.
            new("rate", 0, 0, spark.PadRight(sparkWidth) + rateFigures, false),
        ];
    }

    private static int BarWidth(int width, string figures) => Math.Max(0, width - LabelWidth - figures.Length);

    private static string Mb(long mb) => mb >= 1024
        ? string.Create(Inv, $"{mb / 1024.0:0.0}GB")
        : string.Create(Inv, $"{mb:N0}MB");

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var normal = GetAttributeForRole(VisualRole.Normal);
        var lit = new Attribute(Stone, normal.Background);
        var signal = new Attribute(Signal, normal.Background);
        var warn = new Attribute(Warn, normal.Background);
        var dim = new Attribute(Slate, normal.Background);

        int width = Viewport.Width;
        var lines = _snapshot is { } s ? Lines(s, width) : [];
        for (int row = 0; row < 3; row++)
        {
            SetAttribute(normal);
            AddStr(0, row, new string(' ', Math.Max(0, width)));
            if (row >= lines.Length) continue;
            var line = lines[row];
            int x = 0;
            SetAttribute(normal);
            AddStr(x, row, line.Label.PadRight(LabelWidth));
            x += LabelWidth;
            if (line.LitCells > 0)
            {
                SetAttribute(line.Signal ? signal : line.Warn ? warn : lit);
                AddStr(x, row, new string(Lit, line.LitCells));
                x += line.LitCells;
            }
            if (line.DimCells > 0)
            {
                SetAttribute(dim);
                AddStr(x, row, new string(Dim, line.DimCells));
                x += line.DimCells;
            }
            // The rate row's sparkline sits where the others have bars, in the lit tone.
            SetAttribute(row == 2 ? lit : normal);
            AddStr(x, row, line.Figures);
        }
        return true;
    }
}
