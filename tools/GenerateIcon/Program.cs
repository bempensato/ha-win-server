// Generates src/HaWinServer/Resources/app.ico as a proper multi-resolution
// PNG-in-ICO file (16/32/48/256 px), using nothing but System.Drawing - no
// image tooling dependency for a one-off asset. Run with:
//   dotnet run --project tools/GenerateIcon

var sizes = new[] { 16, 32, 48, 256 };
var repoRoot = FindRepoRoot(AppContext.BaseDirectory);
var outputPath = Path.Combine(repoRoot, "src", "HaWinServer", "Resources", "app.ico");
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

var pngFrames = sizes.Select(size => (Size: size, Png: RenderPng(size))).ToList();
WriteIco(outputPath, pngFrames);

Console.WriteLine($"Wrote {outputPath} ({pngFrames.Count} sizes: {string.Join(", ", sizes)})");
return;

static string FindRepoRoot(string startDir)
{
    var dir = new DirectoryInfo(startDir);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HaWinServer.sln")))
    {
        dir = dir.Parent;
    }
    return dir?.FullName ?? throw new InvalidOperationException("Could not locate repo root (HaWinServer.sln not found).");
}

static byte[] RenderPng(int size)
{
    using var bitmap = new Bitmap(size, size);
    using (var g = Graphics.FromImage(bitmap))
    {
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        // Rounded-square background, flat "home automation" blue.
        var backgroundColor = Color.FromArgb(255, 45, 111, 168);
        using var backgroundBrush = new SolidBrush(backgroundColor);
        var radius = size * 0.22f;
        using var backgroundPath = RoundedRect(new RectangleF(0, 0, size, size), radius);
        g.FillPath(backgroundBrush, backgroundPath);

        // Simple house glyph in white, centered.
        using var glyphBrush = new SolidBrush(Color.White);
        var roofPoints = new[]
        {
            new PointF(size * 0.20f, size * 0.52f),
            new PointF(size * 0.50f, size * 0.24f),
            new PointF(size * 0.80f, size * 0.52f),
        };
        g.FillPolygon(glyphBrush, roofPoints);

        var body = new RectangleF(size * 0.28f, size * 0.50f, size * 0.44f, size * 0.28f);
        g.FillRectangle(glyphBrush, body);

        var doorColor = backgroundColor;
        using var doorBrush = new SolidBrush(doorColor);
        var door = new RectangleF(size * 0.45f, size * 0.62f, size * 0.10f, size * 0.16f);
        g.FillRectangle(doorBrush, door);
    }

    using var stream = new MemoryStream();
    bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
    return stream.ToArray();
}

static System.Drawing.Drawing2D.GraphicsPath RoundedRect(RectangleF bounds, float radius)
{
    var diameter = radius * 2f;
    var path = new System.Drawing.Drawing2D.GraphicsPath();
    path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
    path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
    path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
    path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
    path.CloseFigure();
    return path;
}

static void WriteIco(string path, List<(int Size, byte[] Png)> frames)
{
    using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
    using var writer = new BinaryWriter(stream);

    // ICONDIR
    writer.Write((ushort)0);   // reserved
    writer.Write((ushort)1);   // type = icon
    writer.Write((ushort)frames.Count);

    const int iconDirSize = 6;
    const int entrySize = 16;
    var dataOffset = iconDirSize + entrySize * frames.Count;

    // ICONDIRENTRY[]
    foreach (var frame in frames)
    {
        var dim = frame.Size >= 256 ? 0 : frame.Size; // 0 means 256 per the ICO spec
        writer.Write((byte)dim);          // width
        writer.Write((byte)dim);          // height
        writer.Write((byte)0);            // color count (0 = no palette, true color)
        writer.Write((byte)0);            // reserved
        writer.Write((ushort)1);          // color planes
        writer.Write((ushort)32);         // bits per pixel
        writer.Write((uint)frame.Png.Length);
        writer.Write((uint)dataOffset);
        dataOffset += frame.Png.Length;
    }

    // image data, in the same order as the directory entries
    foreach (var frame in frames)
    {
        writer.Write(frame.Png);
    }
}
