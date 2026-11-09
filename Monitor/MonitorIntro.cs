using System.Diagnostics;
using Indx.Api;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace IndxServer.Monitor;

/// <summary>
/// The monitor's way in, before its window: the indx mark in voxels (<see cref="VoxelMark"/>),
/// large on an empty screen, arriving and turning while the datasets load; then it turns to face
/// the eye, flattens and flies to the header, where it lands as the logo, pixel for pixel; then
/// the window's frame draws itself in round it, the outer border and the two panes, their titles
/// with them, and the monitor takes over on exactly those lines.
///
/// <para>It waits for the arrival, and for the datasets that load at startup (pinned and timed,
/// one at a time): while any is loading or waiting its turn and none is ready, the mark keeps
/// turning. Two quiet lines go with it: under the mark, what is happening right now, the dataset by
/// name, loading or indexing, and how far (<see cref="Working"/>); at the foot, what this is and
/// where to reach it, the versions and the address. Real figures, both: no "booting up", the
/// server is already listening by now. Any key goes straight on. With nothing to load it is the
/// arrival and the way in, about five seconds.</para>
///
/// <para>It draws everything itself, the frame too, rather than animating the window's own views:
/// the window's border is drawn outside anything a view inside it can reach. So the frame's
/// geometry is the window's (<see cref="MonitorWindow.DatasetsHeight"/>, the logo's place, the
/// line styles), and a change to the window's layout wants the same change here.</para>
/// </summary>
internal sealed class MonitorIntro : Window
{
    /// <summary>The flight to the logo, and the frame drawing in, in seconds.</summary>
    internal const double LandSeconds = 1.3, FrameSeconds = 0.9;

    private readonly IApplication _app;
    private readonly Func<MonitorSnapshot?> _readLatest;
    private readonly string _about;
    private readonly VoxelMark _mark = new(Environment.TickCount);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly int? _storedDatasetsHeight = MonitorLayout.Load().DatasetsHeight;
    private bool _skip;
    /// <summary>When the flight to the logo began, and the pose it began from.</summary>
    private double? _landAt;
    private VoxelMark.Pose _from;

    internal MonitorIntro(IApplication app, Func<MonitorSnapshot?> readLatest, MonitorEndpoints? endpoints = null)
    {
        _app = app;
        _readLatest = readLatest;
        // (the console's own version line, so the two always agree)
        _about = endpoints?.Web is { Length: > 0 } web ? $"{Shared.AdminNav.VersionLine}   {web}" : Shared.AdminNav.VersionLine;
        BorderStyle = LineStyle.None;
        CanFocus = true;
        app.AddTimeout(TimeSpan.FromMilliseconds(50), Tick);
    }

    protected override bool OnKeyDown(Key key)
    {
        _skip = true;
        return true;
    }

    private bool Tick()
    {
        double t = _clock.Elapsed.TotalSeconds;
        if (_landAt is null && (_skip || t >= VoxelMark.ArriveSeconds + 0.4 && !StillLoading(_readLatest())))
        {
            _landAt = t;
            _from = Free(t);
        }
        if (_landAt is { } at && t >= at + LandSeconds + FrameSeconds)
        {
            _app.RequestStop();
            return false;
        }
        SetNeedsDraw();
        return true;
    }

    /// <summary>Datasets still loading and none ready, or no word yet (the collector's first
    /// snapshot is a second in): worth waiting for.</summary>
    internal static bool StillLoading(MonitorSnapshot? s) => s is null || MonitorWindow.Loading(s);

    /// <summary>The mark on its own: in the middle of the screen, as large as reads well and no
    /// larger, arriving and turning.</summary>
    private VoxelMark.Pose Free(double t)
    {
        int cols = Viewport.Width, rows = Viewport.Height * 2;
        double scale = _mark.FitScale(Math.Min(cols * 0.6, 96), Math.Min(rows * 0.6, 60));
        return new VoxelMark.Pose(VoxelMark.Arrived(t), VoxelMark.Angle(t), VoxelMark.Tilt, scale, cols / 2.0, rows / 2.0);
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        int cols = Viewport.Width, rows = Viewport.Height;
        if (cols <= 0 || rows <= 0) return true;
        double t = _clock.Elapsed.TotalSeconds;

        var pose = Free(t);
        double landed = 0, frame = 0;
        if (_landAt is { } at)
        {
            landed = Smooth(Math.Clamp((t - at) / LandSeconds, 0, 1));
            frame = Math.Clamp((t - at - LandSeconds) / FrameSeconds, 0, 1);
            // To the logo's place in the window (inside its border): face on (the nearest whole
            // turn), untilted, one pixel a voxel, the cubes in if a key cut the arrival short. The
            // size goes by its logarithm, so the shrinking reads as one steady move away.
            var from = _from;
            double turn = Math.Round(from.Angle / Math.Tau) * Math.Tau;
            pose = new VoxelMark.Pose(
                from.Arrival + (1 - from.Arrival) * landed,
                Lerp(from.Angle, turn, landed),
                Lerp(from.Tilt, 0, landed),
                Math.Exp(Lerp(Math.Log(from.Scale), 0, landed)),
                Lerp(from.X, 1 + MonitorWindow.LogoX + PixlIcon.Width / 2.0, landed),
                Lerp(from.Y, 2 * (1 + MonitorWindow.LogoY) + PixlIcon.Height / 2.0, landed));
        }

        var (_, _, light) = VoxelMarkView.Ground(GetAttributeForRole(VisualRole.Normal));
        VoxelMarkView.Paint(this, _mark.Render(cols, rows * 2, pose, light), cols, rows, toText: landed);

        // While it waits, quietly: what is happening under the mark, a step up the ladder; what
        // this is and where, at the foot, a step further down. Both go as it leaves for the header.
        if (_landAt is null)
        {
            var normal = GetAttributeForRole(VisualRole.Normal);
            var (ground, ink, _) = VoxelMarkView.Ground(normal);
            if (_readLatest() is { } s && MonitorWindow.Loading(s) && Working(s) is { } now)
            {
                SetAttribute(new Attribute(VoxelMarkView.Mix(ground, ink, 5 / 8.0), normal.Background));
                Centred(now, (int)Math.Min(rows - 4, rows / 2.0 + pose.Scale * 2 + 2));
            }
            SetAttribute(new Attribute(VoxelMarkView.Mix(ground, ink, 3 / 8.0), normal.Background));
            Centred(_about, rows - 2);
        }

        if (frame > 0) DrawFrame(cols, rows, frame);
        return true;
    }

    /// <summary>
    /// What is happening now, in a line: the dataset being worked on, by name, what to it, and how
    /// far, "loading films  38%"; with how many are waiting their turn after it, ", 4 more". Before
    /// warm-up has reached the first, "warming up, 5 datasets". Null with nothing on its way up.
    /// </summary>
    internal static string? Working(MonitorSnapshot s)
    {
        int queued = s.Datasets.Count(d => d.Queued);
        var busy = s.Datasets.FirstOrDefault(d => d.State == SystemState.Indexing)
                   ?? s.Datasets.FirstOrDefault(d => d.State == SystemState.Loading);
        if (busy is null)
            return queued > 0 ? $"warming up, {queued} dataset{(queued == 1 ? "" : "s")}" : null;
        string what = busy.State == SystemState.Indexing ? "indexing" : "loading";
        string how = busy.ProgressPercent is { } p ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"  {p}%") : "";
        return $"{what} {busy.DataSetName}{how}" + (queued > 0 ? $", {queued} more" : "");
    }

    private void Centred(string text, int y)
    {
        int cols = Viewport.Width;
        if (text.Length > cols) text = text[..cols];
        AddStr((cols - text.Length) / 2, y, text);
    }

    /// <summary>
    /// The window's frame, <paramref name="f"/> of the way drawn in: each box goes round from its
    /// top left corner, clockwise, its title in its top edge as it passes; the outer border first,
    /// the two panes overlapping it, a beat behind. The boxes are where the window puts them.
    /// </summary>
    private void DrawFrame(int cols, int rows, double f)
    {
        SetAttribute(GetAttributeForRole(VisualRole.Normal));
        Box(0, 0, cols, rows, "indx monitor", rounded: false, Math.Clamp(f / 0.6, 0, 1));

        // Inside the border: the header, then the two panes to the row above the endpoints line
        int inside = rows - 2;
        if (MonitorWindow.DatasetsHeight(inside, _storedDatasetsHeight) is not { } datasets) return;
        int top = 1 + MonitorWindow.HeaderHeight, events = inside - 2 - MonitorWindow.HeaderHeight - datasets;
        double g = Math.Clamp((f - 0.3) / 0.7, 0, 1);
        Box(1, top, cols - 2, datasets, "Datasets", rounded: true, g);
        if (events >= 2) Box(1, top + datasets, cols - 2, events, "Events", rounded: true, g);
    }

    private void Box(int x, int y, int w, int h, string title, bool rounded, double f)
    {
        if (w < 2 || h < 2 || f <= 0) return;
        string corners = rounded ? "╭╮╯╰" : "┌┐┘└";
        // The top edge as the window draws it: the corner, the title between ┤ and ├, the rule
        var topEdge = (corners[0] + "┤" + title + "├").PadRight(w - 1, '─')[..(w - 1)] + corners[1];
        var cells = new List<(int X, int Y, char C)>(2 * (w + h));
        for (int i = 0; i < w; i++) cells.Add((x + i, y, topEdge[i]));
        for (int j = 1; j < h; j++) cells.Add((x + w - 1, y + j, j == h - 1 ? corners[2] : '│'));
        for (int i = w - 2; i >= 0; i--) cells.Add((x + i, y + h - 1, i == 0 ? corners[3] : '─'));
        for (int j = h - 2; j >= 1; j--) cells.Add((x, y + j, '│'));
        int shown = (int)Math.Round(f * cells.Count);
        for (int k = 0; k < shown; k++) AddStr(cells[k].X, cells[k].Y, cells[k].C.ToString());
    }

    private static double Smooth(double u) => u * u * (3 - 2 * u);
    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
}
