namespace IndxServer.Monitor;

/// <summary>
/// The indx mark in voxels, turning slowly: the monitor's loading picture. Each of the mark's
/// 7 by 5 pixels is a cube; the cubes fly in from scattered places, tumbling, settle into the
/// mark, and then it turns about its upright axis, a little tilted so the tops show.
///
/// <para>A port of the Voxel piece in the Indx visuals (<c>ARCT4N/Code/clients/Indx/voxel.js</c>,
/// its solid style), which is the same mark on the license portal's sign-in page. Flat on
/// purpose, as the brand is: each face is one level of a nine-step ladder by how it faces a fixed
/// light, painted far to near, no gradients and no perspective.</para>
///
/// <para>This class only computes levels, 0 (nothing) to 8 (the most light), one per pixel, so a
/// test can read a frame without a terminal; <see cref="VoxelMarkView"/> draws them.</para>
/// </summary>
internal sealed class VoxelMark
{
    /// <summary>The arrival, in seconds: every cube has landed by then.</summary>
    internal const double ArriveSeconds = 3.5;
    /// <summary>One whole turn, in seconds. Slow: it is something to rest the eye on while the
    /// datasets load, not a spinner asking to be watched.</summary>
    internal const double TurnSeconds = 24;

    // The look, as the visuals' defaults: the tilt, the ladder's span for the faces, how far the
    // light wraps round (the faces away from it a step or two darker, not black), and how much of
    // the box the mark's widest turn fills.
    internal const double Tilt = 0.35;
    private const double Soft = 0.6, Fit = 0.8;
    private const int Lo = 3, Hi = 8;

    private static readonly (int[] N, double[][] Q)[] Faces =
    [
        ([0, 0, -1], [[0, 0, 0], [1, 0, 0], [1, 1, 0], [0, 1, 0]]),
        ([0, 0, 1], [[0, 0, 1], [0, 1, 1], [1, 1, 1], [1, 0, 1]]),
        ([-1, 0, 0], [[0, 0, 0], [0, 1, 0], [0, 1, 1], [0, 0, 1]]),
        ([1, 0, 0], [[1, 0, 0], [1, 0, 1], [1, 1, 1], [1, 1, 0]]),
        ([0, -1, 0], [[0, 0, 0], [0, 0, 1], [1, 0, 1], [1, 0, 0]]),
        ([0, 1, 0], [[0, 1, 0], [1, 1, 0], [1, 1, 1], [0, 1, 1]]),
    ];

    /// <summary>The way to the light, in view space: up and to the left, in front (y is down,
    /// the eye at −z).</summary>
    private static readonly double[] Light = Unit([-0.45, -0.65, -0.6]);

    /// <summary>
    /// Where the mark is and how it sits: how far the arrival has got (0 to 1), its turn about
    /// the upright axis and its tilt, in radians, the pixels a voxel, and its middle, in pixels.
    /// </summary>
    internal readonly record struct Pose(double Arrival, double Angle, double Tilt, double Scale, double X, double Y);

    private sealed record Cube(double[] P, int[] Faces, double[] From, double Start, double[] Axis, double Spin);

    private readonly Cube[] _cubes;
    private readonly int _w, _h;

    /// <param name="seed">Where the cubes come in from and how they tumble.</param>
    internal VoxelMark(int seed)
    {
        // The mark's own pixels, the header's logo: one source for both.
        var mark = PixlIcon.Get("IndxLogo");
        _w = PixlIcon.Width; _h = PixlIcon.Height;
        bool Filled(int x, int y) => mark?[x, y] ?? false;

        var rnd = new Random(seed);
        var cubes = new List<Cube>();
        for (int y = 0; y < _h; y++)
            for (int x = 0; x < _w; x++)
            {
                if (!Filled(x, y)) continue;
                // The faces that show once it has landed: none against a neighbour. One deep, so
                // front and back always show.
                var faces = Enumerable.Range(0, Faces.Length)
                    .Where(i => Faces[i].N[2] != 0 || !Filled(x + Faces[i].N[0], y + Faces[i].N[1]))
                    .ToArray();
                double a = rnd.NextDouble() * Math.Tau, b = (rnd.NextDouble() - 0.5) * Math.PI, d = 6 + rnd.NextDouble() * 8;
                cubes.Add(new Cube(
                    [x - _w / 2.0, y - _h / 2.0, -0.5], faces,
                    [Math.Cos(a) * Math.Cos(b) * d, Math.Sin(b) * d, Math.Sin(a) * Math.Cos(b) * d],
                    rnd.NextDouble() * 0.45,
                    // and on the way it tumbles about an axis of its own, half a turn to a turn and a half
                    Unit([rnd.NextDouble() - 0.5, rnd.NextDouble() - 0.5, rnd.NextDouble() - 0.5]),
                    (rnd.NextDouble() < 0.5 ? -1 : 1) * Math.PI * (1 + rnd.NextDouble() * 2)));
            }
        _cubes = [.. cubes];
    }

    /// <summary>
    /// One frame, <paramref name="seconds"/> after it started: <paramref name="cols"/> by
    /// <paramref name="rows"/> levels, row by row. The mark is centred, its widest turn sized to
    /// the box but never past <paramref name="maxCols"/> by <paramref name="maxRows"/>, so it stays
    /// a picture in a large pane rather than filling it.
    ///
    /// <para>On a light ground (<paramref name="lightGround"/>) the ladder runs towards dark ink,
    /// so the light is turned round: the faces towards it are the palest there too.</para>
    /// </summary>
    internal byte[] Render(int cols, int rows, double seconds, bool lightGround = false, int maxCols = 44, int maxRows = 30)
    {
        var pose = new Pose(Arrived(seconds), Angle(seconds), Tilt, FitScale(Math.Min(cols, maxCols), Math.Min(rows, maxRows)), cols / 2.0, rows / 2.0);
        return Render(cols, rows, pose, lightGround);
    }

    /// <summary>How far the arrival has got, <paramref name="seconds"/> in: 0 to 1.</summary>
    internal static double Arrived(double seconds) => Smooth(Math.Clamp(seconds / ArriveSeconds, 0, 1));

    /// <summary>The turn, <paramref name="seconds"/> in. A whole turn faces the eye.</summary>
    internal static double Angle(double seconds) => Math.Tau * seconds / TurnSeconds;

    /// <summary>The pixels a voxel for the mark's widest turn to fill <see cref="Fit"/> of a box.</summary>
    internal double FitScale(double cols, double rows)
    {
        double r = Math.Sqrt(_w / 2.0 * (_w / 2.0) + 0.25);
        return Fit * Math.Min(cols / (2 * r), rows / (_h + 2 * r * Math.Sin(Tilt)));
    }

    /// <summary>One frame in a given pose: <paramref name="cols"/> by <paramref name="rows"/> levels.
    /// Face on, untilted, one pixel a voxel and centred on a pixel corner, it is the logo exactly.</summary>
    internal byte[] Render(int cols, int rows, Pose pose, bool lightGround = false)
    {
        var levels = new byte[Math.Max(0, cols) * Math.Max(0, rows)];
        if (cols <= 0 || rows <= 0) return levels;

        double scale = pose.Scale, cx = pose.X, cy = pose.Y, p = pose.Arrival;
        double ct = Math.Cos(pose.Angle), st = Math.Sin(pose.Angle), cl = Math.Cos(pose.Tilt), sl = Math.Sin(pose.Tilt);
        // The turn about y, then the tilt about x.
        double[] Rot(double[] v)
        {
            double x1 = v[0] * ct + v[2] * st, z1 = -v[0] * st + v[2] * ct;
            return [x1, v[1] * cl - z1 * sl, v[1] * sl + z1 * cl];
        }

        var quads = new List<(double Mid, byte Level, (double X, double Y)[] Pts)>();
        foreach (var c in _cubes)
        {
            double u = Smooth(Math.Clamp((p - c.Start) / 0.5, 0, 1));
            if (u <= 0) continue;
            var o = new double[3];
            for (int i = 0; i < 3; i++) o[i] = c.P[i] + c.From[i] * (1 - u);
            // Its tumble, about its own middle, slowing to nothing as it lands. The shading is by
            // where a face points, so without it the cubes would arrive already lit as they sit.
            double spin = c.Spin * (1 - u);
            // (in flight a step or two down the ladder, and apart from the others: all six faces)
            int dim = (int)Math.Round((1 - u) * 3);
            IEnumerable<int> faces = u < 1 ? Enumerable.Range(0, Faces.Length) : c.Faces;
            foreach (int f in faces)
            {
                var (fn, fq) = Faces[f];
                var n = Rot(Turn(c.Axis, spin, [fn[0], fn[1], fn[2]]));
                if (n[2] >= -1e-6) continue;   // facing away from the eye

                var pts = new (double, double)[4];
                double mid = 0;
                for (int k = 0; k < 4; k++)
                {
                    var q = Turn(c.Axis, spin, [fq[k][0] - 0.5, fq[k][1] - 0.5, fq[k][2] - 0.5]);
                    var w = Rot([o[0] + q[0] + 0.5, o[1] + q[1] + 0.5, o[2] + q[2] + 0.5]);
                    pts[k] = (cx + w[0] * scale, cy + w[1] * scale);
                    mid += w[2] / 4;
                }
                double lit = Math.Max(0, (n[0] * Light[0] + n[1] * Light[1] + n[2] * Light[2] + Soft) / (1 + Soft));
                if (lightGround) lit = 1 - lit;
                int level = Math.Max(1, (int)Math.Round(Lo + (Hi - Lo) * lit) - dim);
                quads.Add((mid, (byte)level, pts));
            }
        }

        // Far to near: the cubes are all one size, so their faces' middles sort them.
        quads.Sort((a, b) => b.Mid.CompareTo(a.Mid));
        foreach (var q in quads) Fill(levels, cols, rows, q.Pts, q.Level);
        return levels;
    }

    /// <summary>A convex face onto the grid: every pixel whose centre it covers.</summary>
    private static void Fill(byte[] levels, int cols, int rows, (double X, double Y)[] pts, byte level)
    {
        double top = pts.Min(q => q.Y), bottom = pts.Max(q => q.Y);
        int y0 = Math.Max(0, (int)Math.Floor(top)), y1 = Math.Min(rows - 1, (int)Math.Ceiling(bottom));
        Span<double> xs = stackalloc double[4];
        for (int y = y0; y <= y1; y++)
        {
            double py = y + 0.5;
            int n = 0;
            for (int i = 0; i < pts.Length; i++)
            {
                var (ax, ay) = pts[i];
                var (bx, by) = pts[(i + 1) % pts.Length];
                if ((ay <= py && by > py) || (by <= py && ay > py))
                    xs[n++] = ax + (py - ay) / (by - ay) * (bx - ax);
            }
            if (n < 2) continue;
            xs[..n].Sort();
            int from = Math.Max(0, (int)Math.Ceiling(xs[0] - 0.5)), to = Math.Min(cols - 1, (int)Math.Floor(xs[n - 1] - 0.5));
            for (int x = from; x <= to; x++) levels[y * cols + x] = level;
        }
    }

    /// <summary>A turn by <paramref name="a"/> about the unit axis <paramref name="k"/> (Rodrigues).</summary>
    private static double[] Turn(double[] k, double a, double[] v)
    {
        if (a == 0) return v;
        double c = Math.Cos(a), s = Math.Sin(a);
        double d = (k[0] * v[0] + k[1] * v[1] + k[2] * v[2]) * (1 - c);
        return
        [
            v[0] * c + (k[1] * v[2] - k[2] * v[1]) * s + k[0] * d,
            v[1] * c + (k[2] * v[0] - k[0] * v[2]) * s + k[1] * d,
            v[2] * c + (k[0] * v[1] - k[1] * v[0]) * s + k[2] * d,
        ];
    }

    private static double Smooth(double u) => u * u * (3 - 2 * u);

    private static double[] Unit(double[] v)
    {
        double l = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
        return l > 0 ? [v[0] / l, v[1] / l, v[2] / l] : v;
    }
}
