using System.Collections.Concurrent;
using HelixToolkit.SharpDX.Assimp;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace UI.Services;

public static class PreviewFormatRegistry
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".apng", ".avif", ".bmp", ".blp", ".dib", ".dds", ".exr", ".gif", ".hdr", ".heic", ".heif",
        ".blp2", ".ico", ".jfif", ".jpe", ".jpeg", ".jpg", ".jxl", ".ktx", ".ktx2", ".pbm", ".pfm", ".pgm",
        ".pic", ".png", ".pnm", ".ppm", ".psd", ".qoi", ".tga", ".tif", ".tiff", ".txd", ".vtf", ".webp",
        ".ytd"
    };

    private static readonly HashSet<string> ModelExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".3d", ".3ds", ".3mf", ".ac", ".ac3d", ".ase", ".assbin", ".b3d", ".blend", ".bvh", ".cob", ".dae",
        ".dxf", ".fbx", ".glb", ".gltf", ".ifc", ".irr", ".irrmesh", ".lwo", ".lws", ".md2", ".md3", ".md5mesh",
        ".mesh", ".nff", ".obj", ".off", ".ogex", ".ply", ".pmx", ".prj", ".q3o", ".q3s", ".raw", ".scn",
        ".sib", ".smd", ".stl", ".ter", ".x", ".x3d", ".usd", ".usdz"
    };

    private static readonly ConcurrentDictionary<string, Func<Stream, int, byte[]?>> ImageProviders = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Func<string, HelixToolkitScene?>> ModelImporters = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path)) || ImageProviders.ContainsKey(Path.GetExtension(path));
    public static bool IsModel(string path) => ModelExtensions.Contains(Path.GetExtension(path)) || ModelImporters.ContainsKey(Path.GetExtension(path));
    public static bool IsTexture(string path) => TextureImageDecoder.Supports(Path.GetExtension(path));

    public static void RegisterImageProvider(string extension, Func<Stream, int, byte[]?> provider)
    {
        ImageProviders[NormalizeExtension(extension)] = provider;
    }

    public static void RegisterModelImporter(string extension, Func<string, HelixToolkitScene?> importer)
    {
        ModelImporters[NormalizeExtension(extension)] = importer;
    }

    public static byte[]? DecodeImage(string path, int size)
    {
        using var stream = File.OpenRead(path);
        return DecodeImage(stream, Path.GetExtension(path), size);
    }

    public static byte[]? DecodeImageOriginal(string path)
    {
        using var stream = File.OpenRead(path);
        return DecodeImage(stream, Path.GetExtension(path), 0);
    }

    internal static IReadOnlyList<DecodedTextureImage> DecodeImagesOriginal(string path)
    {
        using var stream = File.OpenRead(path);
        var extension = Path.GetExtension(path);
        if (TextureImageDecoder.Supports(extension))
            return TextureImageDecoder.DecodeAll(stream, extension, 0);

        var pngBytes = DecodeImage(stream, extension, 0) ??
            throw new NotSupportedException($"No decoder is available for {extension}.");
        return [new DecodedTextureImage(null, pngBytes)];
    }

    public static byte[]? DecodeImage(Stream stream, string extension, int size)
    {
        if (ImageProviders.TryGetValue(extension, out var provider))
            return provider(stream, size);
        if (!ImageExtensions.Contains(extension))
            return null;

        if (stream.CanSeek)
            stream.Position = 0;
        if (TextureImageDecoder.Supports(extension))
            return TextureImageDecoder.Decode(stream, extension, size);

        using var image = Image.Load<Rgba32>(stream);
        const int maxSide = 4096;
        if (image.Width > maxSide || image.Height > maxSide || (long)image.Width * image.Height > 40_000_000)
            throw new InvalidDataException("Image exceeds the preview size limit.");
        if (size > 0)
        {
            image.Mutate(context => context.Resize(new ResizeOptions
            {
                Size = new Size(size, size),
                Mode = ResizeMode.Max
            }));
        }
        using var output = new MemoryStream();
        image.SaveAsPng(output);
        return output.ToArray();
    }

    public static HelixToolkitScene? ImportModel(string path)
    {
        var extension = Path.GetExtension(path);
        if (ModelImporters.TryGetValue(extension, out var importer))
            return importer(path);
        if (!ModelExtensions.Contains(extension))
            return null;
        return new Importer().Load(path);
    }

    private static string NormalizeExtension(string extension)
    {
        var normalized = extension.Trim();
        if (!normalized.StartsWith('.'))
            normalized = "." + normalized;
        return normalized;
    }
}
