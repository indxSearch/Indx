using System.Reflection;
using System.Text.RegularExpressions;

namespace IndxServer.Monitor;

/// <summary>
/// An indx pixl icon as pixels. Every pixl icon is drawn on a 7 by 5 grid with one SVG path made
/// of straight moves (M, H, V, L, Z) on integer coordinates, so rasterising it is a point-in-
/// polygon test at each pixel centre, under the path's own fill rule. No SVG library needed.
///
/// The icons are not copied into this project: the .razor files of the Blazor port
/// (IndxServer/Indx.Systm.Blazor/Icons) are embedded as resources at build time, so a new or
/// changed icon is here the moment it exists there.
///
/// This file lives in IndxServer and IndxWorkbench compiles it from here with a linked
/// &lt;Compile Include&gt;, rather than either project holding a copy. Each assembly embeds the
/// icons itself, so each reads its own resources and the rasteriser is shared source.
///
/// In a terminal one character cell is about twice as tall as it is wide. The half-block
/// characters split a cell into an upper and a lower half, which makes square pixels: 7 by 5
/// pixels become 7 columns by 3 rows of text. That is the smallest size with solid pixels.
/// Braille (2 x 4 dots per cell) would halve it and keep the pixels square, and was tried: the
/// dots do not read as the same icon. Quadrants and sextants have pixels that are not square.
/// </summary>
public sealed partial class PixlIcon
{
    public const int Width = 7, Height = 5;
    public const int TextRows = 3;

    public string Name { get; }
    private readonly bool[,] _pixels = new bool[Height, Width];

    public bool this[int x, int y] => x >= 0 && x < Width && y >= 0 && y < Height && _pixels[y, x];

    private PixlIcon(string name, string pathData, bool evenOdd)
    {
        Name = name;
        var polygons = ParsePath(pathData);
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
                _pixels[y, x] = Inside(polygons, x + 0.5, y + 0.5, evenOdd);
    }

    /// <summary>One line of text per call: row 0..2, each character one column of two pixels.</summary>
    public string TextRow(int row)
    {
        Span<char> line = stackalloc char[Width];
        for (int x = 0; x < Width; x++)
        {
            bool top = this[x, row * 2], bottom = this[x, row * 2 + 1];
            line[x] = (top, bottom) switch { (true, true) => '█', (true, false) => '▀', (false, true) => '▄', _ => ' ' };
        }
        return new string(line);
    }

    // ── Loading ──────────────────────────────────────────────────────────────

    private static readonly Lazy<Dictionary<string, PixlIcon>> All = new(LoadAll);

    public static IReadOnlyCollection<string> Names => All.Value.Keys;

    /// <summary>The icon of that name (as in the pixl library, e.g. "Search"), or null.</summary>
    public static PixlIcon? Get(string name) => All.Value.GetValueOrDefault(name);

    private static Dictionary<string, PixlIcon> LoadAll()
    {
        var icons = new Dictionary<string, PixlIcon>(StringComparer.OrdinalIgnoreCase);
        var assembly = Assembly.GetExecutingAssembly();
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith("pixl/", StringComparison.Ordinal)) continue;
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            var svg = reader.ReadToEnd();
            var path = PathTag().Match(svg);
            var data = PathData().Match(path.Value);
            if (!data.Success) continue;
            var name = Path.GetFileNameWithoutExtension(resource["pixl/".Length..]);
            icons[name] = new PixlIcon(name, data.Groups[1].Value, path.Value.Contains("evenodd", StringComparison.Ordinal));
        }
        return icons;
    }

    [GeneratedRegex(@"<path\b[^>]*>")] private static partial Regex PathTag();
    [GeneratedRegex("\\sd=\"([^\"]*)\"")] private static partial Regex PathData();
    [GeneratedRegex(@"[MHVLZ]|-?\d+")] private static partial Regex Tokens();

    // ── Geometry ─────────────────────────────────────────────────────────────

    private static List<List<(int X, int Y)>> ParsePath(string d)
    {
        var polygons = new List<List<(int, int)>>();
        List<(int X, int Y)>? current = null;
        int x = 0, y = 0;
        char command = 'M';
        var numbers = new Queue<int>();

        void Flush()
        {
            while (numbers.Count > 0)
            {
                switch (command)
                {
                    case 'M':
                        if (numbers.Count < 2) { numbers.Clear(); return; }
                        x = numbers.Dequeue(); y = numbers.Dequeue();
                        current = [(x, y)]; polygons.Add(current);
                        command = 'L'; // further pairs after a move are lines
                        break;
                    case 'L':
                        if (numbers.Count < 2) { numbers.Clear(); return; }
                        x = numbers.Dequeue(); y = numbers.Dequeue(); current?.Add((x, y));
                        break;
                    case 'H': x = numbers.Dequeue(); current?.Add((x, y)); break;
                    case 'V': y = numbers.Dequeue(); current?.Add((x, y)); break;
                    default: numbers.Clear(); break;
                }
            }
        }

        foreach (Match token in Tokens().Matches(d))
        {
            if (char.IsLetter(token.Value[0]))
            {
                Flush();
                command = token.Value[0];
                if (command == 'Z' && current is { Count: > 0 }) { (x, y) = current[0]; }
            }
            else numbers.Enqueue(int.Parse(token.Value));
        }
        Flush();
        return polygons;
    }

    private static bool Inside(List<List<(int X, int Y)>> polygons, double px, double py, bool evenOdd)
    {
        int winding = 0, crossings = 0;
        foreach (var polygon in polygons)
        {
            for (int i = 0; i < polygon.Count; i++)
            {
                var (x1, y1) = polygon[i];
                var (x2, y2) = polygon[(i + 1) % polygon.Count];
                if ((y1 <= py) == (y2 <= py)) continue;                    // edge does not span the ray
                double atX = x1 + (py - y1) / (y2 - y1) * (x2 - x1);
                if (atX <= px) continue;                                   // crossing is to the left
                crossings++;
                winding += y2 > y1 ? 1 : -1;
            }
        }
        return evenOdd ? (crossings & 1) == 1 : winding != 0;
    }
}
