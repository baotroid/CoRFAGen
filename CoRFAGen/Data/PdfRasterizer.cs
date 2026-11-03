using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Docnet.Core;
using Docnet.Core.Models;
using Docnet.Core.Readers;

namespace CoRFAGen.Data
{
    /// <summary>One rendered PDF page (and optional zoomed tiles) plus its text layer.</summary>
    public class RasterizedPage
    {
        public int PageNumber { get; set; }
        public int WidthPx { get; set; }
        public int HeightPx { get; set; }
        public byte[] Png { get; set; }
        public List<byte[]> Tiles { get; } = new List<byte[]>();
        public string Text { get; set; }
    }

    public class RasterizedPdf
    {
        public string FileName { get; set; }
        public int PageCount { get; set; }
        public List<RasterizedPage> Pages { get; } = new List<RasterizedPage>();
        public string Error { get; set; }
    }

    /// <summary>
    /// Renders PDF pages to PNG with pdfium (Docnet.Core) for providers that do not accept PDF files
    /// (Qwen). Vector drawings are rendered locally; the embedded text layer (dimension values,
    /// title block, technical data) is extracted as well, so nothing important is lost.
    /// </summary>
    public static class PdfRasterizer
    {
        public const int MaxPages = 3;              // pages per PDF sent to the model
        public const int PageLongSidePx = 3600;     // ≈9 Mpx for A-format sheets; Qwen accepts up to 16 Mpx per image
        public const int TileSourceLongSidePx = 3400; // high-res render that is cut into 2x2 tiles
        public const int MaxTiledPages = 2;
        public const int MaxTextCharsPerPdf = 8000;

        private static readonly ConditionalWeakTable<byte[], RasterizedPdf> Cache = new ConditionalWeakTable<byte[], RasterizedPdf>();
        private static readonly object Sync = new object();
        private static bool _nativeChecked;
        private static string _nativeError;

        public static RasterizedPdf Rasterize(InputAttachment pdf, bool makeTiles = false)
        {
            lock (Sync)
            {
                if (Cache.TryGetValue(pdf.Bytes, out RasterizedPdf cached)) return cached;

                var result = new RasterizedPdf { FileName = pdf.FileName };
                try
                {
                    string nativeError = EnsureNative();
                    if (nativeError != null) { result.Error = nativeError; return result; }
                    RenderAll(pdf.Bytes, result, makeTiles);
                }
                catch (Exception ex)
                {
                    result.Error = $"PDF rendering failed: {ex.GetType().Name}: {ex.Message}";
                }
                Cache.Add(pdf.Bytes, result);
                return result;
            }
        }

        private static void RenderAll(byte[] bytes, RasterizedPdf result, bool makeTiles)
        {
            // 1st pass at scale 1.0: page sizes in points + text layer
            var sizes = new List<(double w, double h)>();
            using (IDocReader doc = DocLib.Instance.GetDocReader(bytes, new PageDimensions(1.0)))
            {
                result.PageCount = doc.GetPageCount();
                int n = Math.Min(result.PageCount, MaxPages);
                for (int i = 0; i < n; i++)
                {
                    using (IPageReader page = doc.GetPageReader(i))
                    {
                        sizes.Add((Math.Max(1, page.GetPageWidth()), Math.Max(1, page.GetPageHeight())));
                        string text = null;
                        try { text = page.GetText(); } catch { }
                        result.Pages.Add(new RasterizedPage { PageNumber = i + 1, Text = NormalizeText(text) });
                    }
                }
            }

            for (int i = 0; i < result.Pages.Count; i++)
            {
                RasterizedPage rp = result.Pages[i];
                double longSide = Math.Max(sizes[i].w, sizes[i].h);

                // full page overview
                byte[] png = RenderPage(bytes, i, PageLongSidePx / longSide, out int w, out int h, out BitmapSource _);
                rp.Png = png; rp.WidthPx = w; rp.HeightPx = h;

                // zoomed 2x2 tiles so small dimension text stays legible after the provider downscales images
                if (makeTiles && i < MaxTiledPages)
                {
                    RenderPage(bytes, i, TileSourceLongSidePx / longSide, out int tw, out int th, out BitmapSource big);
                    foreach (byte[] tile in CutTiles(big, tw, th))
                        rp.Tiles.Add(tile);
                }
            }
        }

        private static byte[] RenderPage(byte[] bytes, int index, double scale, out int width, out int height, out BitmapSource bitmap)
        {
            using (IDocReader doc = DocLib.Instance.GetDocReader(bytes, new PageDimensions(scale)))
            using (IPageReader page = doc.GetPageReader(index))
            {
                width = page.GetPageWidth();
                height = page.GetPageHeight();
                byte[] bgra = page.GetImage(); // BGRA, transparent background

                // composite onto white (pdfium leaves the paper transparent)
                for (int p = 0; p < bgra.Length; p += 4)
                {
                    int a = bgra[p + 3];
                    if (a == 255) continue;
                    int inv = 255 - a;
                    bgra[p] = (byte)((bgra[p] * a + 255 * inv) / 255);
                    bgra[p + 1] = (byte)((bgra[p + 1] * a + 255 * inv) / 255);
                    bgra[p + 2] = (byte)((bgra[p + 2] * a + 255 * inv) / 255);
                    bgra[p + 3] = 255;
                }

                bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bgra, width * 4);
                bitmap.Freeze();
                return EncodePng(bitmap);
            }
        }

        private static IEnumerable<byte[]> CutTiles(BitmapSource src, int width, int height)
        {
            int halfW = width / 2, halfH = height / 2;
            int ovX = width / 20, ovY = height / 20; // 5% overlap so text on the seam is not cut
            var rects = new[]
            {
                new Int32Rect(0, 0, Math.Min(width, halfW + ovX), Math.Min(height, halfH + ovY)),
                new Int32Rect(Math.Max(0, halfW - ovX), 0, width - Math.Max(0, halfW - ovX), Math.Min(height, halfH + ovY)),
                new Int32Rect(0, Math.Max(0, halfH - ovY), Math.Min(width, halfW + ovX), height - Math.Max(0, halfH - ovY)),
                new Int32Rect(Math.Max(0, halfW - ovX), Math.Max(0, halfH - ovY), width - Math.Max(0, halfW - ovX), height - Math.Max(0, halfH - ovY)),
            };
            foreach (Int32Rect r in rects)
            {
                if (r.Width <= 0 || r.Height <= 0) continue;
                var crop = new CroppedBitmap(src, r);
                crop.Freeze();
                yield return EncodePng(crop);
            }
        }

        private static byte[] EncodePng(BitmapSource bmp)
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var ms = new MemoryStream())
            {
                enc.Save(ms);
                return ms.ToArray();
            }
        }

        private static string NormalizeText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var sb = new StringBuilder(text.Length);
            bool lastSpace = false;
            foreach (char c in text)
            {
                bool ws = char.IsWhiteSpace(c) && c != '\n';
                if (c == '\r') continue;
                if (ws) { if (!lastSpace) sb.Append(' '); lastSpace = true; }
                else { sb.Append(c); lastSpace = false; }
            }
            return sb.ToString().Trim();
        }

        /// <summary>Text layer of all rendered pages, limited in size.</summary>
        public static string GetText(RasterizedPdf pdf)
        {
            var sb = new StringBuilder();
            foreach (RasterizedPage p in pdf.Pages)
            {
                if (string.IsNullOrWhiteSpace(p.Text)) continue;
                sb.AppendLine($"--- page {p.PageNumber} ---");
                sb.AppendLine(p.Text);
            }
            string s = sb.ToString();
            return s.Length > MaxTextCharsPerPdf ? s.Substring(0, MaxTextCharsPerPdf) + "\n[...text truncated]" : s;
        }

        // ------------------------------------------------------------------ native pdfium

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibrary(string path);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string name);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern int GetModuleFileName(IntPtr module, StringBuilder buffer, int size);

        /// <summary>
        /// Loads pdfium.dll shipped with the add-in before Docnet's P/Invoke resolves it.
        /// If another (possibly incompatible) pdfium.dll is already loaded into Revit, rendering is skipped
        /// rather than risking a crash. Returns null on success or an explanation.
        /// </summary>
        private static string EnsureNative()
        {
            if (_nativeChecked) return _nativeError;
            _nativeChecked = true;
            try
            {
                string asmDir = Path.GetDirectoryName(typeof(PdfRasterizer).Assembly.Location) ?? "";
                string[] candidates =
                {
                    Path.Combine(asmDir, "pdfium.dll"),
                    Path.Combine(asmDir, "runtimes", "win-x64", "native", "pdfium.dll"),
                };
                string ours = candidates.FirstOrDefault(File.Exists);

                IntPtr existing = GetModuleHandle("pdfium.dll");
                if (existing != IntPtr.Zero)
                {
                    var sb = new StringBuilder(1024);
                    GetModuleFileName(existing, sb, sb.Capacity);
                    string loaded = sb.ToString();
                    if (ours == null || !string.Equals(Path.GetFullPath(loaded), Path.GetFullPath(ours), StringComparison.OrdinalIgnoreCase))
                    {
                        _nativeError = $"another pdfium.dll is already loaded in Revit ({loaded}); PDF rendering skipped";
                        return _nativeError;
                    }
                    return null;
                }

                if (ours == null)
                {
                    _nativeError = "pdfium.dll not found next to CoRFAGen.dll (reinstall the add-in)";
                    return _nativeError;
                }
                if (LoadLibrary(ours) == IntPtr.Zero)
                {
                    _nativeError = $"pdfium.dll could not be loaded (Win32 error {Marshal.GetLastWin32Error()})";
                    return _nativeError;
                }
                return null;
            }
            catch (Exception ex)
            {
                _nativeError = "pdfium initialisation failed: " + ex.Message;
                return _nativeError;
            }
        }
    }
}
