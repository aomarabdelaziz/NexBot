namespace NexBot.App.Theme;

/// <summary>Dark, gold-edged drawing for context menus (the map menu).</summary>
internal sealed class SroMenuRenderer : ToolStripProfessionalRenderer
{
    public SroMenuRenderer() : base(new Colors()) => RoundedEdges = false;

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = !e.Item.Enabled ? SroTheme.Muted : e.Item.Selected ? SroTheme.Gold : SroTheme.Text;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = SroTheme.GoldDim;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var r = new Rectangle(e.ImageRectangle.X + 2, e.ImageRectangle.Y + 2, e.ImageRectangle.Width - 4, e.ImageRectangle.Height - 4);
        using var pen = new Pen(SroTheme.Gold, 2f);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        e.Graphics.DrawLines(pen, [new Point(r.Left + 2, r.Top + r.Height / 2), new Point(r.Left + r.Width / 3 + 1, r.Bottom - 3), new Point(r.Right - 2, r.Top + 3)]);
    }

    private sealed class Colors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => SroTheme.Bg;
        public override Color MenuBorder => SroTheme.GoldDim;
        public override Color MenuItemBorder => SroTheme.GoldDim;
        public override Color MenuItemSelected => SroTheme.InputBack;
        public override Color MenuItemSelectedGradientBegin => SroTheme.InputBack;
        public override Color MenuItemSelectedGradientEnd => SroTheme.InputBack;
        public override Color ImageMarginGradientBegin => SroTheme.Bg;
        public override Color ImageMarginGradientMiddle => SroTheme.Bg;
        public override Color ImageMarginGradientEnd => SroTheme.Bg;
        public override Color SeparatorDark => Color.FromArgb(70, SroTheme.GoldDim);
        public override Color SeparatorLight => Color.Transparent;
        public override Color CheckBackground => SroTheme.InputBack;
        public override Color CheckSelectedBackground => SroTheme.InputBack;
        public override Color CheckPressedBackground => SroTheme.InputBack;
    }
}
