namespace BeGenSt.Data
{
    /// <summary>
    /// A source file sent to the AI model: a raster image (photo, render, scan)
    /// or a PDF drawing (vector or raster) with orthographic views.
    /// </summary>
    public class InputAttachment
    {
        public string FilePath { get; set; }
        public string FileName { get; set; }
        public byte[] Bytes { get; set; }
        public string MimeType { get; set; }

        public bool IsPdf => MimeType == "application/pdf";
        public bool IsImage => MimeType != null && MimeType.StartsWith("image/");

        public static readonly string[] SupportedExtensions = { ".jpg", ".jpeg", ".png", ".webp", ".pdf" };

        public const string OpenFileFilter =
            "Images and PDF drawings|*.jpg;*.jpeg;*.png;*.webp;*.pdf|" +
            "PDF drawings|*.pdf|" +
            "Images|*.jpg;*.jpeg;*.png;*.webp|" +
            "All Files|*.*";

        public static InputAttachment FromFile(string path)
        {
            return new InputAttachment
            {
                FilePath = path,
                FileName = Path.GetFileName(path),
                Bytes = File.ReadAllBytes(path),
                MimeType = GetMimeType(path)
            };
        }

        public static string GetMimeType(string path)
        {
            string ext = Path.GetExtension(path)?.ToLowerInvariant();
            switch (ext)
            {
                case ".pdf": return "application/pdf";
                case ".png": return "image/png";
                case ".webp": return "image/webp";
                default: return "image/jpeg";
            }
        }

        public static string GetExtension(string mimeType)
        {
            switch (mimeType)
            {
                case "application/pdf": return ".pdf";
                case "image/png": return ".png";
                case "image/webp": return ".webp";
                default: return ".jpg";
            }
        }

        public static bool IsSupported(string path)
        {
            string ext = Path.GetExtension(path)?.ToLowerInvariant();
            return SupportedExtensions.Contains(ext);
        }

        /// <summary>Quick sanity check that a .pdf file really starts with the %PDF header.</summary>
        public static bool LooksLikePdf(byte[] bytes)
        {
            return bytes != null && bytes.Length > 4 &&
                   bytes[0] == (byte)'%' && bytes[1] == (byte)'P' && bytes[2] == (byte)'D' && bytes[3] == (byte)'F';
        }
    }
}
