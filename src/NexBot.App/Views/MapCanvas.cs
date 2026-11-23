using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using NexBot.Game;
using NexBot.Automation;
using NexBot.Configuration;
using NexBot.Map;
using NexBot.Media;
using static NexBot.App.Theme.SroTheme;

namespace NexBot.App.Views;

/// <summary>
/// The world map (ported from POC-2's map page): minimap tiles from Media.pk2 under markers for players, mobs, NPCs,
/// pets, drops and teleports. Wheel zooms, drag pans, a click walks there (the target shows as a cross).
/// </summary>
internal sealed class MapCanvas : Control
{
    private static readonly int[] Zooms = [64, 96, 128, 192, 256, 384, 512]; // pixels per tile
    private const int MaxBitmaps = 300;
    private const double DefaultX = (168 - 135) * MinimapLayer.TileSize + 96; // Jangan
    private const double DefaultY = (96 - 92) * MinimapLayer.TileSize + 96;

    private readonly Dictionary<string, Bitmap?> _bitmaps = [];
    private readonly HashSet<string> _loading = [];
    private readonly SemaphoreSlim _decoders = new(2);
    private readonly Font _labelFont = new("Segoe UI", 8f);
    private readonly Font _regionFont = Title(10f);
    private MediaImages? _tiles;
    private int _generation;
    private int _zoomIndex = 3;
    private MapSnapshot? _snapshot;
    private double _cx = DefaultX, _cy = DefaultY;
    private string? _layer;
    private bool _dungeon;
    private bool _follow = true;
    private (double X, double Y)? _target;
    private DateTime _lastSnapshot;
    private IReadOnlyList<WalkCommand> _script = [];
    private IReadOnlyList<Position> _route = [];
    private int _scriptIndex = -1;
    private Point _mouseDown;
    private Point? _mouse;
    private bool _dragging;
    private (double X, double Y) _dragStart;
    private TrainingAreaSettings? _area;
    private readonly List<MapPoint> _draft = [];
    private StatsSnapshot? _stats;

    public MapCanvas()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                 | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Hex("#0b0a08");
        Cursor = Cursors.Cross;
    }

    /// <summary>Walks to a world point; returns an error message or null.</summary>
    public Func<double, double, string?>? WalkRequested { get; set; }

    /// <summary>Raised with a short message for the status line (walk errors, targets).</summary>
    public event Action<string>? Message;

    /// <summary>Right click without dragging: the screen point and the world point under it (for the map menu).</summary>
    public event Action<Point, double, double>? RightClicked;

    /// <summary>The polygon recording finished (with the corners clicked) or the area changed.</summary>
    public event Action? TrainingAreaChanged;

    /// <summary>The training area to draw (circle, or polygon when used).</summary>
    public TrainingAreaSettings? TrainingArea
    {
        get => _area;
        set
        {
            _area = value;
            Invalidate();
        }
    }

    /// <summary>While true, left clicks add polygon corners instead of walking.</summary>
    public bool RecordingPolygon { get; private set; }

    /// <summary>The statistics box (null hides it).</summary>
    public StatsSnapshot? Stats
    {
        get => _stats;
        set
        {
            _stats = value;
            Invalidate();
        }
    }

    public void StartPolygon()
    {
        _draft.Clear();
        RecordingPolygon = true;
        Message?.Invoke("Recording: click the corners clockwise, then press Stop recording.");
        Invalidate();
    }

    /// <summary>Stops recording; with 3 or more corners they become the training area's polygon (and it is used).</summary>
    public void StopPolygon()
    {
        if (!RecordingPolygon)
            return;
        RecordingPolygon = false;
        if (_area is not null && _draft.Count >= 3)
        {
            _area.Polygon = [.. _draft];
            _area.UsePolygon = true;
            if (!_area.IsSet && _snapshot is { } s)
                (_area.X, _area.Y, _area.Region) = (s.Me.X, s.Me.Y, s.Me.Region);
            Message?.Invoke($"Polygon saved with {_draft.Count} corners and set as the training area.");
            TrainingAreaChanged?.Invoke();
        }
        else
        {
            Message?.Invoke("A polygon needs at least 3 corners: nothing was saved.");
        }
        _draft.Clear();
        Invalidate();
    }

    public void ClearPolygon()
    {
        RecordingPolygon = false;
        _draft.Clear();
        if (_area is not null)
        {
            _area.Polygon = [];
            _area.UsePolygon = false;
            TrainingAreaChanged?.Invoke();
        }
        Message?.Invoke("Polygon cleared: the training area is the circle again.");
        Invalidate();
    }

    public MediaImages? Tiles
    {
        get => _tiles;
        set
        {
            _tiles = value;
            _generation++;
            ClearBitmaps();
            Invalidate();
        }
    }

    public bool Follow
    {
        get => _follow;
        set
        {
            _follow = value;
            if (value && _snapshot is { } s)
                (_cx, _cy) = (s.Me.X, s.Me.Y);
            Invalidate();
        }
    }

    public void ZoomBy(int steps, Point? anchor = null)
    {
        int next = Math.Clamp(_zoomIndex + steps, 0, Zooms.Length - 1);
        if (next == _zoomIndex)
            return;
        if (anchor is { } a && !_follow)
        {
            // Keep the world point under the mouse still.
            var before = ToWorld(a);
            _zoomIndex = next;
            var after = ToWorld(a);
            _cx += before.X - after.X;
            _cy += before.Y - after.Y;
        }
        else
        {
            _zoomIndex = next;
        }
        Invalidate();
    }

    /// <summary>Shows a walk script's path (null = none); <paramref name="currentIndex"/> marks the line being walked.</summary>
    public void SetScript(IReadOnlyList<WalkCommand>? commands, int currentIndex = -1)
    {
        _script = commands ?? [];
        _scriptIndex = currentIndex;
        Invalidate();
    }

    /// <summary>Shows a route the bot found by itself (town trip without a script); empty = none.</summary>
    public void SetRoute(IReadOnlyList<Position> route)
    {
        if (ReferenceEquals(route, _route) || route.Count == 0 && _route.Count == 0)
            return;
        _route = route;
        Invalidate();
    }

    public void SetSnapshot(MapSnapshot? snapshot)
    {
        // A read that collided with a server update gives null for one tick: keep the last picture (and the walk
        // line) unless the character is really gone.
        if (snapshot is null && _snapshot is not null && DateTime.UtcNow - _lastSnapshot < TimeSpan.FromSeconds(2))
            return;
        if (snapshot is not null)
            _lastSnapshot = DateTime.UtcNow;
        _snapshot = snapshot;
        if (snapshot is not null)
        {
            if (snapshot.Me.Layer != _layer || snapshot.Me.Dungeon != _dungeon)
            {
                // Entered or left a dungeon: other tiles, other coordinates.
                _layer = snapshot.Me.Layer;
                _dungeon = snapshot.Me.Dungeon;
                _target = null;
                _follow = true;
            }
            if (_follow)
                (_cx, _cy) = (snapshot.Me.X, snapshot.Me.Y);
            if (_target is { } t && Math.Abs(t.X - snapshot.Me.X) < 3 && Math.Abs(t.Y - snapshot.Me.Y) < 3)
                _target = null;
        }
        else
        {
            _target = null;
        }
        Invalidate();
    }

    private double PixelsPerUnit => Zooms[_zoomIndex] / MinimapLayer.TileSize;

    private PointF ToScreen(double x, double y) =>
        new((float)(Width / 2.0 + (x - _cx) * PixelsPerUnit), (float)(Height / 2.0 - (y - _cy) * PixelsPerUnit));

    private (double X, double Y) ToWorld(Point p) =>
        (_cx + (p.X - Width / 2.0) / PixelsPerUnit, _cy - (p.Y - Height / 2.0) / PixelsPerUnit);

    #region Painting

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        DrawTiles(g);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        var snap = _snapshot;
        DrawTrainingArea(g, snap);
        DrawScript(g, snap);
        DrawRoute(g, snap);
        if (snap is not null)
        {
            foreach (var entity in snap.Entities)
                DrawEntity(g, entity);
            DrawTarget(g, snap);
            var me = ToScreen(snap.Me.X, snap.Me.Y);
            using (var glow = new SolidBrush(Color.FromArgb(60, Sox)))
                g.FillEllipse(glow, me.X - 11, me.Y - 11, 22, 22);
            Diamond(g, Sox, me.X, me.Y, 6, 7, Color.Black);
        }

        DrawOverlay(g, snap);
        DrawStats(g, snap);
        DrawLegend(g);
        DrawHover(g, snap);
        StrokeRect(g, Hex("#000000"), ClientRectangle);
    }

    private void DrawTiles(Graphics g)
    {
        var tiles = _tiles;
        double size = MinimapLayer.TileSize;
        double halfW = Width / 2.0 / PixelsPerUnit, halfH = Height / 2.0 / PixelsPerUnit;
        int c0 = (int)Math.Floor((_cx - halfW) / size), c1 = (int)Math.Floor((_cx + halfW) / size);
        int r0 = (int)Math.Floor((_cy - halfH) / size), r1 = (int)Math.Floor((_cy + halfH) / size);
        int offX = _dungeon ? 0 : 135, offY = _dungeon ? 0 : 92;
        float px = Zooms[_zoomIndex];

        if (_bitmaps.Count > MaxBitmaps)
            ClearBitmaps();

        g.InterpolationMode = InterpolationMode.Bilinear;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        using var grid = new Pen(Color.FromArgb(28, Gold));
        for (int col = c0; col <= c1; col++)
        {
            for (int row = r0; row <= r1; row++)
            {
                var topLeft = ToScreen(col * size, (row + 1) * size);
                var rect = new RectangleF(MathF.Floor(topLeft.X), MathF.Floor(topLeft.Y), MathF.Ceiling(px) + 1, MathF.Ceiling(px) + 1);
                var bitmap = tiles is null ? null : GetBitmap(tiles, _layer, col + offX, row + offY);
                if (bitmap is not null)
                    g.DrawImage(bitmap, rect);
                else
                    g.DrawRectangle(grid, rect.X, rect.Y, px, px);
            }
        }
        g.PixelOffsetMode = PixelOffsetMode.Default;
    }

    private static Color KindColor(MapEntityKind kind) => kind switch
    {
        MapEntityKind.Player => Hex("#6fb8ff"),
        MapEntityKind.Mob => Hex("#e05050"),
        MapEntityKind.Unique => Hex("#c070ff"),
        MapEntityKind.Npc => Hex("#5fd27a"),
        MapEntityKind.Pet => Hex("#ffa040"),
        MapEntityKind.Drop => Hex("#e8e8e8"),
        MapEntityKind.PartyMember => Hex("#ff7ab8"),
        _ => Hex("#40d0d0"),
    };

    internal static string KindName(MapEntityKind kind) => kind switch
    {
        MapEntityKind.Npc => "NPC",
        MapEntityKind.Pet => "Pet / horse",
        MapEntityKind.PartyMember => "Party member",
        _ => kind.ToString(),
    };

    private void DrawEntity(Graphics g, MapEntity entity)
    {
        var p = ToScreen(entity.X, entity.Y);
        if (p.X < -10 || p.Y < -10 || p.X > Width + 10 || p.Y > Height + 10)
            return;
        var color = KindColor(entity.Kind);
        using var fill = new SolidBrush(color);
        using var outline = new Pen(Color.FromArgb(220, 0, 0, 0), 1.2f);
        switch (entity.Kind)
        {
            case MapEntityKind.Drop:
                g.FillRectangle(fill, p.X - 2.5f, p.Y - 2.5f, 5, 5);
                g.DrawRectangle(outline, p.X - 2.5f, p.Y - 2.5f, 5, 5);
                break;
            case MapEntityKind.Teleport:
                Diamond(g, color, p.X, p.Y, 6, 6, Color.Black);
                break;
            case MapEntityKind.Unique:
                g.FillEllipse(fill, p.X - 6, p.Y - 6, 12, 12);
                g.DrawEllipse(outline, p.X - 6, p.Y - 6, 12, 12);
                using (var ring = new Pen(Color.FromArgb(120, color), 1.5f))
                    g.DrawEllipse(ring, p.X - 10, p.Y - 10, 20, 20);
                break;
            case MapEntityKind.PartyMember:
                g.FillEllipse(fill, p.X - 5, p.Y - 5, 10, 10);
                g.DrawEllipse(outline, p.X - 5, p.Y - 5, 10, 10);
                if (entity.Name.Length > 0)
                    TextRenderer.DrawText(g, entity.Name, _labelFont, new Point((int)p.X + 7, (int)p.Y - 7), color);
                break;
            default:
                float r = entity.Kind == MapEntityKind.Player ? 4.5f : 3.8f;
                g.FillEllipse(fill, p.X - r, p.Y - r, r * 2, r * 2);
                g.DrawEllipse(outline, p.X - r, p.Y - r, r * 2, r * 2);
                break;
        }
    }

    private void DrawTarget(Graphics g, MapSnapshot snap)
    {
        if (_target is not { } t)
            return;
        var me = ToScreen(snap.Me.X, snap.Me.Y);
        var p = ToScreen(t.X, t.Y);
        using (var shadow = new Pen(Color.FromArgb(150, 0, 0, 0), 4f))
            g.DrawLine(shadow, me, p);
        using (var dash = new Pen(Sox, 2f) { DashStyle = DashStyle.Dash })
            g.DrawLine(dash, me, p);
        using (var ring = new Pen(Color.FromArgb(140, Sox), 1.5f))
            g.DrawEllipse(ring, p.X - 9, p.Y - 9, 18, 18);
        using (var cross = new Pen(Sox, 2.2f))
        {
            g.DrawLine(cross, p.X - 6, p.Y - 6, p.X + 6, p.Y + 6);
            g.DrawLine(cross, p.X - 6, p.Y + 6, p.X + 6, p.Y - 6);
        }
        // Remaining distance, updated as the character walks.
        double left = Math.Sqrt((t.X - snap.Me.X) * (t.X - snap.Me.X) + (t.Y - snap.Me.Y) * (t.Y - snap.Me.Y));
        DistanceTag(g, p, string.Create(CultureInfo.InvariantCulture, $"{left:0} m"), Sox);
    }

    /// <summary>
    /// The script path: gold line through the MOVE points (broken at teleports), numbered start, a portal ring where a
    /// teleport or return scroll is used, and the point being walked to highlighted.
    /// </summary>
    /// <summary>The training area belongs to this map (same dungeon, or both in the open world).</summary>
    private static bool SameLayer(TrainingAreaSettings area, MapSnapshot? snap)
    {
        if (snap is null)
            return true;
        bool areaDungeon = Position.IsDungeonRegion(area.Region);
        return snap.Me.Dungeon ? area.Region == snap.Me.Region : !areaDungeon;
    }

    private void DrawTrainingArea(Graphics g, MapSnapshot? snap)
    {
        var color = Hex("#7ddc6a");
        if (_area is { } area && SameLayer(area, snap))
        {
            using var fill = new SolidBrush(Color.FromArgb(38, color));
            using var edge = new Pen(Color.FromArgb(210, color), 1.6f) { DashStyle = DashStyle.Dash };
            if (area.UsePolygon && area.HasPolygon)
            {
                var points = area.Polygon.Select(p => ToScreen(p.X, p.Y)).ToArray();
                g.FillPolygon(fill, points);
                g.DrawPolygon(edge, points);
            }
            else if (area.IsSet)
            {
                var c = ToScreen(area.X, area.Y);
                float r = (float)(area.Radius * PixelsPerUnit);
                g.FillEllipse(fill, c.X - r, c.Y - r, r * 2, r * 2);
                g.DrawEllipse(edge, c.X - r, c.Y - r, r * 2, r * 2);
            }
            if (area.IsSet)
            {
                var c = ToScreen(area.X, area.Y);
                using var cross = new Pen(color, 2f);
                g.DrawLine(cross, c.X - 5, c.Y, c.X + 5, c.Y);
                g.DrawLine(cross, c.X, c.Y - 5, c.X, c.Y + 5);
            }
        }

        if (RecordingPolygon && _draft.Count > 0)
        {
            var points = _draft.Select(p => ToScreen(p.X, p.Y)).ToList();
            if (_mouse is { } m)
                points.Add(m);
            using var line = new Pen(Hex("#f5dc90"), 2f);
            if (points.Count > 1)
                g.DrawLines(line, points.ToArray());
            using var dot = new SolidBrush(Hex("#f5dc90"));
            foreach (var p in _draft.Select(p => ToScreen(p.X, p.Y)))
                g.FillEllipse(dot, p.X - 3.5f, p.Y - 3.5f, 7, 7);
        }
    }

    private void DrawStats(Graphics g, MapSnapshot? snap)
    {
        if (_stats is not { } s || snap is null)
            return;
        var ci = CultureInfo.CurrentCulture;
        string[] lines =
        [
            string.Create(ci, $"Time  {(int)s.Time.TotalHours}:{s.Time.Minutes:00}:{s.Time.Seconds:00}"),
            string.Create(ci, $"Exp  {s.ExpGained:N0}   ({s.ExpPerHour:N0} / h)"),
            string.Create(ci, $"Gold  {(s.GoldChange >= 0 ? "+" : "")}{s.GoldChange:N0}   ({s.GoldPerHour:N0} / h)"),
            string.Create(ci, $"Kills  {s.Kills:N0}   ({s.KillsPerHour:N0} / h){(s.Uniques > 0 ? $"   ·   uniques {s.Uniques}" : "")}"),
            string.Create(ci, $"Picked up  {s.ItemsPicked:N0}   ·   deaths  {s.Deaths}"),
        ];
        int width = lines.Max(l => TextRenderer.MeasureText(l, _labelFont).Width) + 20;
        var box = new Rectangle(8, 62, width, 22 + lines.Length * 16);
        if (box.Bottom > Height - 44)
            return;
        FillPlate(g, box);
        TextRenderer.DrawText(g, "SESSION", _labelFont, new Point(box.X + 9, box.Y + 5), Gold);
        for (int i = 0; i < lines.Length; i++)
            TextRenderer.DrawText(g, lines[i], _labelFont, new Point(box.X + 9, box.Y + 20 + i * 16), SroThemeText);
    }

    private void DrawScript(Graphics g, MapSnapshot? snap)
    {
        if (_script.Count == 0)
            return;
        bool dungeon = snap?.Me.Dungeon ?? false;
        using var line = new Pen(Color.FromArgb(200, Hex("#e8c97a")), 2f) { LineJoin = LineJoin.Round };
        using var shadow = new Pen(Color.FromArgb(140, 0, 0, 0), 4.5f) { LineJoin = LineJoin.Round };
        using var dot = new SolidBrush(Hex("#f5dc90"));
        using var portal = new Pen(Hex("#b48cff"), 2f);
        PointF? previous = null;
        bool first = true;
        for (int i = 0; i < _script.Count; i++)
        {
            var c = _script[i];
            if (c.Type is WalkCommandType.Buy or WalkCommandType.Sell)
            {
                // A shop stop: a gold coin on the point where it happens (the route goes on from there).
                if (previous is { } shop)
                {
                    using var coin = new SolidBrush(c.Type == WalkCommandType.Buy ? Sox : Good);
                    g.FillEllipse(coin, shop.X - 6, shop.Y - 6, 12, 12);
                    g.DrawEllipse(Pens.Black, shop.X - 6, shop.Y - 6, 12, 12);
                }
                continue;
            }
            if (c.Type is WalkCommandType.Teleport or WalkCommandType.Return)
            {
                if (previous is { } at)
                {
                    g.DrawEllipse(portal, at.X - 8, at.Y - 8, 16, 16);
                    g.DrawEllipse(portal, at.X - 4, at.Y - 4, 8, 8);
                }
                previous = null; // the next point is somewhere else
                continue;
            }
            if (c.Type != WalkCommandType.Move || c.Position.InDungeon != dungeon)
                continue;
            var p = ToScreen(c.Position.PosX, c.Position.PosY);
            if (previous is { } from)
            {
                g.DrawLine(shadow, from, p);
                g.DrawLine(line, from, p);
            }
            if (first)
            {
                Diamond(g, Good, p.X, p.Y, 6, 6, Color.Black); // start
                first = false;
            }
            else
            {
                g.FillEllipse(dot, p.X - 2.5f, p.Y - 2.5f, 5, 5);
            }
            if (i == _scriptIndex)
            {
                using var now = new Pen(Good, 2f);
                g.DrawEllipse(now, p.X - 7, p.Y - 7, 14, 14);
            }
            previous = p;
        }
    }

    /// <summary>The found route: a dashed cyan line from the character through each turn.</summary>
    private void DrawRoute(Graphics g, MapSnapshot? snap)
    {
        if (_route.Count == 0 || snap is null || snap.Me.Dungeon)
            return;
        using var shadow = new Pen(Color.FromArgb(140, 0, 0, 0), 4.5f) { LineJoin = LineJoin.Round };
        using var line = new Pen(Color.FromArgb(230, 90, 220, 255), 2f) { LineJoin = LineJoin.Round, DashStyle = DashStyle.Dash };
        using var dot = new SolidBrush(Color.FromArgb(230, 90, 220, 255));
        var points = new List<PointF> { ToScreen(snap.Me.X, snap.Me.Y) };
        points.AddRange(_route.Select(p => ToScreen(p.PosX, p.PosY)));
        var array = points.ToArray();
        g.DrawLines(shadow, array);
        g.DrawLines(line, array);
        foreach (var p in array.Skip(1))
            g.FillEllipse(dot, p.X - 3, p.Y - 3, 6, 6);
    }

    /// <summary>A small plate with a distance next to a map point.</summary>
    private void DistanceTag(Graphics g, PointF at, string text, Color color)
    {
        var size = TextRenderer.MeasureText(text, BodyBold);
        var box = new Rectangle((int)at.X + 12, (int)at.Y - 26, size.Width + 10, size.Height + 4);
        if (box.Right > Width - 4)
            box.X = (int)at.X - box.Width - 12;
        if (box.Y < 4)
            box.Y = (int)at.Y + 12;
        using (var back = new SolidBrush(Color.FromArgb(215, 8, 7, 6)))
            g.FillRectangle(back, box);
        StrokeRect(g, Color.FromArgb(180, color), box);
        TextRenderer.DrawText(g, text, BodyBold, box, color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    /// <summary>Where a click would walk: a faint line to the cursor with the distance (red past the walk limit).</summary>
    private void DrawWalkPreview(Graphics g, MapSnapshot snap, Point m)
    {
        var (x, y) = ToWorld(m);
        double distance = Math.Sqrt((x - snap.Me.X) * (x - snap.Me.X) + (y - snap.Me.Y) * (y - snap.Me.Y));
        bool tooFar = distance > MapSnapshot.MaxClickWalk;
        var color = tooFar ? Bad : Color.FromArgb(230, 240, 220, 160);
        var me = ToScreen(snap.Me.X, snap.Me.Y);
        using (var pen = new Pen(Color.FromArgb(tooFar ? 150 : 110, color), 1.2f) { DashStyle = DashStyle.Dot })
            g.DrawLine(pen, me, m);
        string text = tooFar
            ? string.Create(CultureInfo.InvariantCulture, $"{distance:0} m · too far (max {MapSnapshot.MaxClickWalk:0})")
            : string.Create(CultureInfo.InvariantCulture, $"{distance:0} m");
        DistanceTag(g, m, text, color);
    }

    private void DrawOverlay(Graphics g, MapSnapshot? snap)
    {
        // Region + coordinates (top left).
        string region = snap is null ? "Not in game" : snap.Me.RegionName ?? (snap.Me.Dungeon ? "Dungeon" : "Region " + snap.Me.Region);
        string coords = snap is null
            ? "The map follows your character once it is in game."
            : string.Create(CultureInfo.InvariantCulture, $"X {snap.Me.X:0}   Y {snap.Me.Y:0}   Z {snap.Me.Z}   ·   R {snap.Me.Region}");
        // The region name uses GDI+ (DrawString): GDI cannot draw the embedded Cinzel font.
        var size1 = g.MeasureString(region.ToUpperInvariant(), _regionFont);
        var size2 = TextRenderer.MeasureText(coords, _labelFont);
        var box = new Rectangle(8, 8, (int)Math.Max(size1.Width + 4, size2.Width) + 18, 46);
        FillPlate(g, box);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using (var gold = new SolidBrush(Gold))
            g.DrawString(region.ToUpperInvariant(), _regionFont, gold, box.X + 7, box.Y + 5);
        TextRenderer.DrawText(g, coords, _labelFont, new Point(box.X + 9, box.Y + 26), Muted);

        // Zoom + follow (top right).
        string zoom = $"{Zooms[_zoomIndex]} px / sector{(_follow ? "" : "   ·   free view")}";
        var zs = TextRenderer.MeasureText(zoom, _labelFont);
        var zbox = new Rectangle(Width - zs.Width - 26, 8, zs.Width + 18, 22);
        FillPlate(g, zbox);
        TextRenderer.DrawText(g, zoom, _labelFont, zbox, Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        if (_tiles is null)
        {
            const string hint = "Select the client folder and read the server config to load the minimap.";
            var hs = TextRenderer.MeasureText(hint, _labelFont);
            var hbox = new Rectangle((Width - hs.Width) / 2 - 12, Height / 2 - 14, hs.Width + 24, 28);
            FillPlate(g, hbox);
            TextRenderer.DrawText(g, hint, _labelFont, hbox, Value, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    private void DrawLegend(Graphics g)
    {
        (string Name, MapEntityKind? Kind)[] items =
        [
            ("You", null), ("Party", MapEntityKind.PartyMember), ("Player", MapEntityKind.Player), ("Mob", MapEntityKind.Mob), ("Unique", MapEntityKind.Unique),
            ("NPC", MapEntityKind.Npc), ("Pet", MapEntityKind.Pet), ("Drop", MapEntityKind.Drop), ("Teleport", MapEntityKind.Teleport),
        ];
        int x = 18, width = 0;
        foreach (var item in items)
            width += 16 + TextRenderer.MeasureText(item.Name, _labelFont).Width + 6;
        var box = new Rectangle(8, Height - 34, width + 14, 26);
        if (box.Right > Width - 8)
            return; // too narrow
        FillPlate(g, box);
        foreach (var item in items)
        {
            float cy = box.Y + box.Height / 2f;
            if (item.Kind is { } kind)
            {
                using var b = new SolidBrush(KindColor(kind));
                g.FillEllipse(b, x - 4, cy - 4, 8, 8);
            }
            else
            {
                Diamond(g, Sox, x, cy, 4, 5, Color.Black);
            }
            TextRenderer.DrawText(g, item.Name, _labelFont, new Point(x + 8, box.Y + 5), SroThemeText);
            x += 16 + TextRenderer.MeasureText(item.Name, _labelFont).Width + 6;
        }
    }

    private static Color SroThemeText => Theme.SroTheme.Text;

    private void DrawHover(Graphics g, MapSnapshot? snap)
    {
        if (snap is null || _mouse is not { } m || _dragging)
            return;
        MapEntity? best = null;
        double bestDistance = 9;
        foreach (var entity in snap.Entities)
        {
            var p = ToScreen(entity.X, entity.Y);
            double d = Math.Sqrt((p.X - m.X) * (p.X - m.X) + (p.Y - m.Y) * (p.Y - m.Y));
            if (d < bestDistance)
                (best, bestDistance) = (entity, d);
        }
        if (best is null)
        {
            DrawWalkPreview(g, snap, m);
            return;
        }

        double distance = Math.Sqrt((best.X - snap.Me.X) * (best.X - snap.Me.X) + (best.Y - snap.Me.Y) * (best.Y - snap.Me.Y));
        string title = string.IsNullOrEmpty(best.Name) ? KindName(best.Kind) : best.Name;
        string detail = string.Create(CultureInfo.InvariantCulture,
            $"{KindName(best.Kind)}{(best.Extra is null || best.Kind == MapEntityKind.Mob && best.Extra == "General" ? "" : " · " + best.Extra)} · {distance:0} away");
        var s1 = TextRenderer.MeasureText(title, BodyBold);
        var s2 = TextRenderer.MeasureText(detail, _labelFont);
        var box = new Rectangle(m.X + 14, m.Y + 12, Math.Max(s1.Width, s2.Width) + 16, 40);
        if (box.Right > Width - 4)
            box.X = m.X - box.Width - 10;
        if (box.Bottom > Height - 4)
            box.Y = m.Y - box.Height - 8;
        FillPlate(g, box, KindColor(best.Kind));
        TextRenderer.DrawText(g, title, BodyBold, new Point(box.X + 8, box.Y + 4), Value);
        TextRenderer.DrawText(g, detail, _labelFont, new Point(box.X + 8, box.Y + 21), Muted);
    }

    private static void FillPlate(Graphics g, Rectangle box, Color? accent = null)
    {
        using (var back = new SolidBrush(Color.FromArgb(215, 12, 11, 9)))
            g.FillRectangle(back, box);
        StrokeRect(g, accent ?? Line, box);
        StrokeRect(g, Color.FromArgb(160, 0, 0, 0), Rectangle.Inflate(box, 1, 1));
    }

    #endregion

    #region Tiles

    private Bitmap? GetBitmap(MediaImages tiles, string? layer, int x, int y)
    {
        string key = $"{layer}|{x}|{y}";
        if (_bitmaps.TryGetValue(key, out var bitmap))
            return bitmap;
        if (!_loading.Add(key))
            return null;

        int generation = _generation;
        _ = Task.Run(async () =>
        {
            Bitmap? decoded = null;
            await _decoders.WaitAsync().ConfigureAwait(false);
            try
            {
                if (tiles.GetTile(layer, x, y) is { } png)
                    decoded = Decode(png);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException or ArgumentException)
            {
                // missing or broken tile: leave it empty
            }
            finally
            {
                _decoders.Release();
            }
            OnTileLoaded(generation, key, decoded);
        });
        return null;
    }

    private static Bitmap Decode(byte[] png)
    {
        using var stream = new MemoryStream(png);
        using var image = new Bitmap(stream);
        var copy = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppPArgb); // fastest to draw
        using (var g = Graphics.FromImage(copy))
            g.DrawImage(image, 0, 0, image.Width, image.Height);
        return copy;
    }

    private void OnTileLoaded(int generation, string key, Bitmap? bitmap)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            bitmap?.Dispose();
            return;
        }
        try
        {
            BeginInvoke(() =>
            {
                _loading.Remove(key);
                if (generation != _generation || IsDisposed)
                {
                    bitmap?.Dispose();
                    return;
                }
                _bitmaps[key] = bitmap;
                Invalidate();
            });
        }
        catch (InvalidOperationException)
        {
            bitmap?.Dispose();
        }
    }

    private void ClearBitmaps()
    {
        foreach (var bitmap in _bitmaps.Values)
            bitmap?.Dispose();
        _bitmaps.Clear();
        _loading.Clear();
        _generation++;
    }

    #endregion

    #region Mouse

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        ZoomBy(e.Delta > 0 ? 1 : -1, e.Location);
        base.OnMouseWheel(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        if (e.Button == MouseButtons.Left)
        {
            _mouseDown = e.Location;
            _dragStart = (_cx, _cy);
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _mouse = e.Location;
        if (e.Button == MouseButtons.Left)
        {
            int dx = e.X - _mouseDown.X, dy = e.Y - _mouseDown.Y;
            if (!_dragging && dx * dx + dy * dy > 16)
            {
                _dragging = true;
                _follow = false;
                Cursor = Cursors.SizeAll;
            }
            if (_dragging)
            {
                _cx = _dragStart.X - dx / PixelsPerUnit;
                _cy = _dragStart.Y + dy / PixelsPerUnit;
            }
        }
        Invalidate();
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            if (_dragging)
            {
                _dragging = false;
                Cursor = Cursors.Cross;
            }
            else if (RecordingPolygon)
            {
                var (x, y) = ToWorld(e.Location);
                _draft.Add(new MapPoint(Math.Round(x, 1), Math.Round(y, 1)));
                Message?.Invoke($"Corner {_draft.Count} added. Press Stop recording when the polygon is closed.");
                Invalidate();
            }
            else
            {
                Walk(ToWorld(e.Location));
            }
        }
        else if (e.Button == MouseButtons.Right)
        {
            var (x, y) = ToWorld(e.Location);
            if (RightClicked is { } menu)
                menu(e.Location, x, y);
            else
                Follow = true;
        }
        base.OnMouseUp(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _mouse = null;
        Invalidate();
        base.OnMouseLeave(e);
    }

    public void Walk((double X, double Y) point)
    {
        if (_snapshot is null)
        {
            Message?.Invoke("The character is not in game.");
            return;
        }
        // null from WalkRequested means the move was sent.
        string? error = WalkRequested is { } walk ? walk(point.X, point.Y) : "Nothing to walk with.";
        if (error is null)
        {
            _target = point;
            Message?.Invoke(string.Create(CultureInfo.InvariantCulture, $"Walking to X {point.X:0}, Y {point.Y:0}"));
        }
        else
        {
            Message?.Invoke(error);
        }
        Invalidate();
    }

    #endregion

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _generation++;
            foreach (var bitmap in _bitmaps.Values)
                bitmap?.Dispose();
            _bitmaps.Clear();
            _labelFont.Dispose();
            _regionFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
