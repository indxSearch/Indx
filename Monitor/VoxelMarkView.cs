using System.Diagnostics;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace IndxServer.Monitor;

/// <summary>
/// Draws <see cref="VoxelMark"/> in the terminal, the way <see cref="PixlIconView"/> draws an
/// icon: a character cell is about twice as tall as it is wide, so the upper half block, its
/// foreground one pixel and its background the one below, makes two square pixels a character.
///
/// <para>The ladder's levels are greys between the view's background and the ink (stone on a dark
/// ground, ink on a light one), so the mark sits on whatever the terminal has. On a terminal that
/// only has 16 colours, where those greys would collapse into two or three, each character is
/// instead one shade, ░ ▒ ▓ █, in the normal text colour: ARCT4N's ascii finish, which still
/// reads as the mark.</para>
///
/// <para>The arrival starts again each time the view is shown. The window asks for a frame on its
/// own timer, and only while the view is visible.</para>
/// </summary>
public sealed class VoxelMarkView : View
{
    private static readonly Color Stone = new(0xCF, 0xCF, 0xCF), Ink = new(0x12, 0x12, 0x15);
    private const string Shades = " ░▒▓█";

    private readonly VoxelMark _mark = new(Environment.TickCount);
    private readonly Stopwatch _clock = new();

    public VoxelMarkView()
    {
        CanFocus = false;
        VisibleChanged += (_, _) => { if (Visible) _clock.Restart(); };
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        int cols = Viewport.Width, rows = Viewport.Height;
        if (cols <= 0 || rows <= 0) return true;
        if (!_clock.IsRunning) _clock.Start();
        var (_, _, light) = Ground(GetAttributeForRole(VisualRole.Normal));
        Paint(this, _mark.Render(cols, rows * 2, _clock.Elapsed.TotalSeconds, light), cols, rows);
        return true;
    }

    /// <summary>
    /// The ground under the mark and its ink: the scheme's background, or, where that is the
    /// terminal's own (no colour), told from the text colour; with neither, a dark ground, which
    /// most terminals have.
    /// </summary>
    internal static (Color Ground, Color Ink, bool Light) Ground(Attribute normal)
    {
        var ground = normal.Background;
        bool light = ground.A != 0 ? !ground.IsDarkColor() : normal.Foreground.A != 0 && normal.Foreground.IsDarkColor();
        if (ground.A == 0) ground = light ? new Color(0xFF, 0xFF, 0xFF) : Ink;
        return (ground, light ? Ink : Stone, light);
    }

    /// <summary>
    /// Levels onto a view, two pixels a character, from its top left. <paramref name="toText"/>
    /// takes them part or all of the way to the view's text colour, every face at the top of the
    /// ladder: at 1 the mark is drawn exactly as <see cref="PixlIconView"/> draws the logo, which
    /// is where the intro lands it.
    /// </summary>
    internal static void Paint(View view, byte[] levels, int cols, int rows, double toText = 0)
    {
        var normal = view.GetAttributeForRole(VisualRole.Normal);
        var (ground, ink, _) = Ground(normal);
        if (toText > 0 && normal.Foreground.A != 0) ink = Mix(ink, normal.Foreground, toText);
        bool shades = view.App?.Driver?.Force16Colors ?? false;
        for (int row = 0; row < rows; row++)
            for (int col = 0; col < cols; col++)
            {
                int top = levels[row * 2 * cols + col], bottom = levels[(row * 2 + 1) * cols + col];
                if (top == 0 && bottom == 0)
                {
                    view.SetAttribute(normal);
                    view.AddStr(col, row, " ");
                }
                else if (shades || toText >= 1)
                {
                    // In shades; or landed, as PixlIconView draws the logo: half blocks in the text
                    // colour itself, which may be the terminal's own and so can't be mixed towards
                    view.SetAttribute(normal);
                    string glyph = !shades || toText >= 1 ? (top > 0, bottom > 0) switch { (true, true) => "█", (true, false) => "▀", _ => "▄" }
                        : Shades[Math.Clamp((int)Math.Round((top + bottom) / 4.0), 1, Shades.Length - 1)].ToString();
                    view.AddStr(col, row, glyph);
                }
                else
                {
                    // A half with nothing in it is the background itself, the terminal's own where
                    // the scheme leaves it: so a lower half alone is the lower half block, as "no
                    // colour" for a foreground is the terminal's text colour, not its ground.
                    var back = bottom == 0 ? normal.Background : Grey(bottom);
                    view.SetAttribute(top == 0 ? new Attribute(Grey(bottom), normal.Background) : new Attribute(Grey(top), back));
                    view.AddStr(col, row, top == 0 ? "▄" : "▀");
                }
            }

        Color Grey(int level) => Mix(ground, ink, (level + (8 - level) * toText) / 8.0);
    }

    internal static Color Mix(Color a, Color b, double t) => new(
        (int)Math.Round(a.R + (b.R - a.R) * t),
        (int)Math.Round(a.G + (b.G - a.G) * t),
        (int)Math.Round(a.B + (b.B - a.B) * t));
}
