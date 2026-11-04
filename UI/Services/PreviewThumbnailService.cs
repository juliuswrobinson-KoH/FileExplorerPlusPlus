using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace UI.Services;

public static class PreviewThumbnailService
{
    public static async Task<ImageSource?> LoadAsync(string path, int size)
    {
        byte[]? bytes = null;
        if (PreviewFormatRegistry.IsImage(path))
        {
            try
            {
                bytes = await Task.Run(() => PreviewFormatRegistry.DecodeImage(path, size));
            }
            catch (UnauthorizedAccessException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or NotSupportedException or SixLabors.ImageSharp.ImageFormatException)
            {
                // Fall through to the Windows thumbnail provider for installed format codecs.
            }
        }

        if (bytes is null)
            bytes = await LoadWindowsThumbnailAsync(path, size);
        if (bytes is null)
            return null;

        var bitmap = new BitmapImage();
        using var stream = new MemoryStream(bytes);
        await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
        return bitmap;
    }

    private static async Task<byte[]?> LoadWindowsThumbnailAsync(string path, int size)
    {
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            using var thumbnail = await file.GetThumbnailAsync(
                Windows.Storage.FileProperties.ThumbnailMode.PicturesView,
                (uint)Math.Clamp(size * 2, 64, 512),
                Windows.Storage.FileProperties.ThumbnailOptions.UseCurrentScale);
            if (thumbnail is null || thumbnail.Size == 0)
                return null;
            using var stream = new MemoryStream();
            await thumbnail.AsStreamForRead().CopyToAsync(stream);
            return stream.ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    public static async Task<string> LoadContentPreviewAsync(string path)
    {
        var bytes = new byte[256];
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var read = await stream.ReadAsync(bytes);
        if (read == 0)
            return "(empty file)";
        Array.Resize(ref bytes, read);

        var encoding = DetectTextEncoding(bytes);
        var text = encoding.GetString(bytes);
        if (text.Length > 0 && text[0] == '\uFEFF')
            text = text[1..];
        if (!text.Any(character => char.IsControl(character) && character is not '\r' and not '\n' and not '\t'))
            return text.Replace("\r", " ").Replace("\n", " ").Trim();

        return Convert.ToHexString(bytes).Chunk(2).Select(pair => new string(pair)).Aggregate("", (result, value) =>
            result.Length == 0 ? value : $"{result} {value}");
    }

    private static System.Text.Encoding DetectTextEncoding(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return new System.Text.UTF8Encoding(false, true);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return System.Text.Encoding.Unicode;
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return System.Text.Encoding.BigEndianUnicode;

        var evenNulls = 0;
        var oddNulls = 0;
        for (var index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] == 0)
            {
                if ((index & 1) == 0)
                    evenNulls++;
                else
                    oddNulls++;
            }
        }
        if (oddNulls > bytes.Length / 8)
            return System.Text.Encoding.Unicode;
        if (evenNulls > bytes.Length / 8)
            return System.Text.Encoding.BigEndianUnicode;

        try
        {
            _ = new System.Text.UTF8Encoding(false, true).GetString(bytes);
            return new System.Text.UTF8Encoding(false, true);
        }
        catch (System.Text.DecoderFallbackException)
        {
            return System.Text.Encoding.Latin1;
        }
    }
}
