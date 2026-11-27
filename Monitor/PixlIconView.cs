using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace IndxServer.Monitor;

/// <summary>Draws one pixl icon, 7 columns by 3 rows, in a colour of its own on the view's background.</summary>
public sealed class PixlIconView : View
{
    private string _icon = "";
    private Color? _color;

    public PixlIconView()
    {
        Width = PixlIcon.Width;
        Height = PixlIcon.TextRows;
        CanFocus = false;
    }

    public string Icon
    {
        get => _icon;
        set { if (_icon == value) return; _icon = value; SetNeedsDraw(); }
    }

    /// <summary>Null draws the icon in the view's normal text colour.</summary>
    public Color? Color
    {
        get => _color;
        set { if (Nullable.Equals(_color, value)) return; _color = value; SetNeedsDraw(); }
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var normal = GetAttributeForRole(VisualRole.Normal);
        SetAttribute(_color is { } c ? new Attribute(c, normal.Background) : normal);
        var icon = PixlIcon.Get(_icon);
        for (int row = 0; row < PixlIcon.TextRows; row++)
            AddStr(0, row, icon?.TextRow(row) ?? new string(' ', PixlIcon.Width));
        return true;
    }
}
