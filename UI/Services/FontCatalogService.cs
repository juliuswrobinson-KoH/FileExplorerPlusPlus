using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace UI.Services;

public static class FontCatalogService
{
    private const uint PrivateFont = 0x10;
    private static readonly string[] SupportedExtensions = [".ttf", ".otf", ".ttc"];
    private static readonly HashSet<string> LoadedFonts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object FontLoadLock = new();

    public static string FontDirectory => Path.Combine(AppContext.BaseDirectory, "Fonts");

    public static IReadOnlyList<string> GetFontFamilies()
    {
        Directory.CreateDirectory(FontDirectory);
        lock (FontLoadLock)
        {
            foreach (var path in Directory.EnumerateFiles(FontDirectory)
                         .Where(path => SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)))
            {
                if (LoadedFonts.Contains(path))
                    continue;
                if (AddFontResourceEx(path, PrivateFont, IntPtr.Zero) == 0)
                    throw new InvalidOperationException($"Could not load custom font '{path}'.");
                LoadedFonts.Add(path);
            }
        }

        using var fonts = new InstalledFontCollection();
        return fonts.Families
            .Select(font => font.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint AddFontResourceEx(string fileName, uint flags, IntPtr reserved);
}
