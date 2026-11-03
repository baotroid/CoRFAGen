using System;
using System.IO;
using CoRFAGen.Data;

// usage: dotnet run -- drawing.dwg [output folder]
// writes <name>-sheet.png, <name>-data.txt and <name>-lines.csv
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args.Length < 1 || !CadDrawingReader.IsCadFile(args[0]))
        {
            Console.Error.WriteLine("usage: dwg_extract drawing.dwg|drawing.dxf [output folder]");
            return 2;
        }

        try
        {
            CadSheet s = CadDrawingReader.Read(args[0]);
            string dir = args.Length > 1 ? args[1] : Path.GetDirectoryName(Path.GetFullPath(args[0]));
            Directory.CreateDirectory(dir);
            string stem = Path.Combine(dir, Path.GetFileNameWithoutExtension(args[0]));
            File.WriteAllBytes(stem + "-sheet.png", s.Png);
            File.WriteAllText(stem + "-data.txt", s.Summary);
            File.WriteAllText(stem + "-lines.csv", s.GeometryCsv);
            Console.WriteLine($"frame found: {s.SheetFrameFound}; dimensions: {s.DimensionCount} (ignored outside the sheet: {s.OutsideDimensions}); " +
                              $"texts: {s.TextCount}; regions: {s.RegionCount}; labels differing from the drawn length: {s.MismatchCount}; " +
                              $"unread custom objects: {s.UnreadObjects}; sheet image: {s.WidthPx}x{s.HeightPx}");
            Console.WriteLine(stem + "-sheet.png, -data.txt, -lines.csv");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }
}
