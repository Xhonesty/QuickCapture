using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace QuickCapture;

internal static class ImageClipboard
{
    internal static async Task SetAsync(byte[] encoded, byte[] bmp, string registered, string mime)
    {
        var data = new FreshImageData(encoded, bmp, registered, mime);
        for (int attempt = 0; ; attempt++)
        {
            try { Clipboard.SetDataObject(data, true); return; }
            catch (COMException) when (attempt < 4) { await Task.Delay(80); }
        }
    }

    // OLE can request a format more than once while persisting clipboard data.
    // Every request gets a fresh stream at position zero, so earlier reads cannot
    // consume a later payload. One OLE transaction persists all formats on exit.
    private sealed class FreshImageData : IDataObject
    {
        private readonly byte[] _encoded, _dib;
        private readonly string _registered, _mime;
        private readonly BitmapSource _bitmap;
        internal FreshImageData(byte[] encoded, byte[] bmp, string registered, string mime)
        { _encoded = encoded; _dib = bmp[14..]; _registered = registered; _mime = mime; _bitmap = ImageExportService.Decode(bmp, ScreenshotFormat.Bmp); }
        public object? GetData(string format, bool autoConvert) => format == DataFormats.Bitmap ? _bitmap : format == DataFormats.Dib ? new MemoryStream(_dib, false) : format == _registered || format == _mime ? new MemoryStream(_encoded, false) : null;
        public object? GetData(string format) => GetData(format, true);
        public object? GetData(Type format) => GetData(format.FullName!);
        public bool GetDataPresent(string format, bool autoConvert) => GetFormats(false).Contains(format);
        public bool GetDataPresent(string format) => GetDataPresent(format, true);
        public bool GetDataPresent(Type format) => GetDataPresent(format.FullName!);
        public string[] GetFormats(bool autoConvert) => new[] { _registered, _mime, DataFormats.Bitmap, DataFormats.Dib };
        public string[] GetFormats() => GetFormats(true);
        public void SetData(object data) => throw new NotSupportedException();
        public void SetData(Type format, object data) => throw new NotSupportedException();
        public void SetData(string format, object data) => throw new NotSupportedException();
        public void SetData(string format, object data, bool autoConvert) => throw new NotSupportedException();
    }
}
