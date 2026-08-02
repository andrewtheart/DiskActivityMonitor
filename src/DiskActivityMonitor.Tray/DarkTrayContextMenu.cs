using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DiskActivityMonitor.Tray;

internal sealed class DarkTrayContextMenu : ContextMenuStrip
{
    internal static readonly Color MenuBackground = Color.FromArgb(20, 22, 25);
    internal static readonly Color MenuForeground = Color.FromArgb(242, 244, 247);
    internal static readonly Color MenuBorder = Color.FromArgb(58, 64, 72);
    internal static readonly Color MenuHover = Color.FromArgb(52, 58, 66);
    internal static readonly Color MenuSeparator = Color.FromArgb(49, 54, 61);
    internal const int MenuCornerRadius = 10;

    public DarkTrayContextMenu()
    {
        AutoSize = true;
        BackColor = MenuBackground;
        ForeColor = MenuForeground;
        Font = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point);
        MinimumSize = new Size(190, 0);
        Padding = new Padding(6);
        ShowCheckMargin = false;
        ShowImageMargin = false;
        Renderer = new DarkTrayMenuRenderer();
    }

    internal ToolStripMenuItem AddCommand(string text, EventHandler onClick)
    {
        var item = new ToolStripMenuItem(text)
        {
            AutoSize = true,
            BackColor = Color.Transparent,
            ForeColor = MenuForeground,
            Margin = new Padding(0, 1, 0, 1),
            Padding = new Padding(12, 7, 18, 7),
        };
        item.Click += onClick;
        Items.Add(item);
        return item;
    }

    internal void AddDivider()
    {
        Items.Add(new ToolStripSeparator
        {
            Margin = new Padding(8, 5, 8, 5),
        });
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyRoundedRegion();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        ApplyRoundedRegion();
    }

    private void ApplyRoundedRegion()
    {
        if (Width <= 0 || Height <= 0)
            return;

        using var path = CreateRoundedPath(new Rectangle(0, 0, Width, Height), MenuCornerRadius);
        var previous = Region;
        Region = new Region(path);
        previous?.Dispose();
    }

    internal static GraphicsPath CreateRoundedPath(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        int diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        if (diameter <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }

        var arc = new Rectangle(bounds.Left, bounds.Top, diameter, diameter);
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class DarkTrayMenuRenderer : ToolStripProfessionalRenderer
{
    public DarkTrayMenuRenderer() : base(new DarkTrayMenuColorTable())
    {
        RoundedEdges = true;
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        e.Graphics.Clear(DarkTrayContextMenu.MenuBackground);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = DarkTrayContextMenu.CreateRoundedPath(
            new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1),
            DarkTrayContextMenu.MenuCornerRadius);
        using var pen = new Pen(DarkTrayContextMenu.MenuBorder);
        e.Graphics.DrawPath(pen, path);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected)
            return;

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = DarkTrayContextMenu.CreateRoundedPath(
            new Rectangle(3, 1, e.Item.Width - 6, e.Item.Height - 2),
            6);
        using var brush = new SolidBrush(DarkTrayContextMenu.MenuHover);
        e.Graphics.FillPath(brush, path);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled
            ? DarkTrayContextMenu.MenuForeground
            : Color.FromArgb(132, 138, 146);
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        int y = e.Item.Height / 2;
        using var pen = new Pen(DarkTrayContextMenu.MenuSeparator);
        e.Graphics.DrawLine(pen, 4, y, e.Item.Width - 4, y);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = DarkTrayContextMenu.MenuForeground;
        base.OnRenderArrow(e);
    }
}

internal sealed class DarkTrayMenuColorTable : ProfessionalColorTable
{
    public override Color ToolStripDropDownBackground => DarkTrayContextMenu.MenuBackground;
    public override Color MenuBorder => DarkTrayContextMenu.MenuBorder;
    public override Color MenuItemBorder => DarkTrayContextMenu.MenuHover;
    public override Color MenuItemSelected => DarkTrayContextMenu.MenuHover;
    public override Color MenuItemSelectedGradientBegin => DarkTrayContextMenu.MenuHover;
    public override Color MenuItemSelectedGradientEnd => DarkTrayContextMenu.MenuHover;
    public override Color SeparatorDark => DarkTrayContextMenu.MenuSeparator;
    public override Color SeparatorLight => DarkTrayContextMenu.MenuSeparator;
    public override Color ImageMarginGradientBegin => DarkTrayContextMenu.MenuBackground;
    public override Color ImageMarginGradientMiddle => DarkTrayContextMenu.MenuBackground;
    public override Color ImageMarginGradientEnd => DarkTrayContextMenu.MenuBackground;
}