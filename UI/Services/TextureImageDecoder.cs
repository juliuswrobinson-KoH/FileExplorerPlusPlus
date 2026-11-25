using BCnEncoder.Decoder;
using BCnEncoder.ImageSharp;
using CodeWalker.GameFiles;
using CodeWalker.Utils;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace UI.Services;

internal sealed record DecodedTextureImage(string? Name, byte[] PngBytes);

internal static class TextureImageDecoder
{
    private const int MaximumDimension = 4096;
    private const long MaximumPixelCount = 40_000_000;
    private const long MaximumInputBytes = 128 * 1024 * 1024;
    private const long MaximumYtdOutputBytes = 512 * 1024 * 1024;
    private const int MaximumYtdTextureCount = 4096;
    private static readonly byte[] Ktx1Identifier =
        [0xAB, 0x4B, 0x54, 0x58, 0x20, 0x31, 0x31, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] Ktx2Identifier =
        [0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A];

    public static bool Supports(string extension) =>
        extension.Equals(".dds", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".ktx", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".ktx2", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".blp", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".blp2", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".vtf", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".txd", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".ytd", StringComparison.OrdinalIgnoreCase);

    public static byte[] Decode(Stream source, string extension, int size)
    {
        var stream = EnsureSeekable(source);
        using var ownedStream = ReferenceEquals(stream, source) ? null : stream;
        if (stream.Length > MaximumInputBytes)
            throw new InvalidDataException("Texture file exceeds the 128 MB decode limit.");
        stream.Position = 0;
        if (extension.Equals(".ytd", StringComparison.OrdinalIgnoreCase))
            return DecodeYtd(stream, size, maximumTextures: 1)[0].PngBytes;

        using var image = extension.ToLowerInvariant() switch
        {
            ".dds" => DecodeDds(stream),
            ".ktx" => DecodeKtx1(stream),
            ".ktx2" => DecodeKtx2(stream),
            ".blp" or ".blp2" => DecodeBlp(stream),
            ".vtf" => DecodeVtf(stream),
            ".txd" => DecodeTxd(stream),
            _ => throw new NotSupportedException($"Unsupported texture format: {extension}")
        };

        ValidateDimensions(image.Width, image.Height);
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

    public static IReadOnlyList<DecodedTextureImage> DecodeAll(Stream source, string extension, int size)
    {
        if (!extension.Equals(".ytd", StringComparison.OrdinalIgnoreCase))
            return [new DecodedTextureImage(null, Decode(source, extension, size))];

        var stream = EnsureSeekable(source);
        using var ownedStream = ReferenceEquals(stream, source) ? null : stream;
        if (stream.Length > MaximumInputBytes)
            throw new InvalidDataException("Texture file exceeds the 128 MB decode limit.");
        stream.Position = 0;
        return DecodeYtd(stream, size);
    }

    private static IReadOnlyList<DecodedTextureImage> DecodeYtd(
        Stream stream, int size, int maximumTextures = int.MaxValue)
    {
        var file = new YtdFile();
        file.Load(ReadAllBytes(stream));
        var textures = file.TextureDict?.Textures?.data_items;
        if (textures is not { Length: > 0 })
            throw new InvalidDataException("The YTD dictionary contains no textures.");
        if (textures.Length > MaximumYtdTextureCount)
            throw new InvalidDataException("The YTD dictionary contains too many textures to decode safely.");

        var decoded = new List<DecodedTextureImage>(textures.Length);
        long totalOutputBytes = 0;
        for (var index = 0; index < textures.Length; index++)
        {
            var texture = textures[index];
            if (texture?.Data?.FullData is not { Length: > 0 })
                continue;

            ValidateDimensions(texture.Width, texture.Height);
            var ddsBytes = DDSIO.GetDDSFile(texture);
            if (ddsBytes.Length > MaximumInputBytes)
                throw new InvalidDataException($"YTD texture '{texture.Name}' exceeds the 128 MB decode limit.");

            using var dds = new MemoryStream(ddsBytes, writable: false);
            using var image = new BcDecoder().DecodeToImageRgba32(dds);
            ValidateDimensions(image.Width, image.Height);
            if (image.Width != texture.Width || image.Height != texture.Height)
                throw new InvalidDataException($"YTD texture '{texture.Name}' has inconsistent dimensions.");
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
            if (totalOutputBytes + output.Length > MaximumYtdOutputBytes)
                throw new InvalidDataException("YTD textures exceed the 512 MB total PNG decode limit.");
            totalOutputBytes += output.Length;
            decoded.Add(new DecodedTextureImage(
                string.IsNullOrWhiteSpace(texture.Name) ? $"texture_{index + 1}" : texture.Name,
                output.ToArray()));
            if (decoded.Count == maximumTextures)
                break;
        }

        if (decoded.Count == 0)
            throw new InvalidDataException("The YTD dictionary contains no decodable textures.");
        return decoded;
    }

    private static byte[] ReadAllBytes(Stream stream)
    {
        if (stream.Length > int.MaxValue)
            throw new InvalidDataException("Texture file is too large to decode.");
        var bytes = new byte[(int)stream.Length];
        stream.Position = 0;
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static Image<Rgba32> DecodeDds(Stream stream)
    {
        RequireSignature(stream, "DDS "u8);
        stream.Position = 12;
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        var height = reader.ReadUInt32();
        var width = reader.ReadUInt32();
        ValidateDimensions(width, height);
        stream.Position = 0;
        return new BcDecoder().DecodeToImageRgba32(stream);
    }

    private static Image<Rgba32> DecodeKtx1(Stream stream)
    {
        RequireSignature(stream, Ktx1Identifier);
        stream.Position = 12;
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        var endianness = reader.ReadUInt32();
        if (endianness != 0x04030201)
            throw new InvalidDataException("Big-endian KTX textures are not supported.");
        var glType = reader.ReadUInt32();
        var glTypeSize = reader.ReadUInt32();
        var glFormat = reader.ReadUInt32();
        var internalFormat = reader.ReadUInt32();
        _ = reader.ReadUInt32();
        var width = reader.ReadUInt32();
        var height = reader.ReadUInt32();
        ValidateDimensions(width, height);
        if (glType == 0 && glFormat == 0 && internalFormat == 0)
            throw new InvalidDataException("KTX texture has an invalid pixel format.");
        if (glTypeSize is not (1 or 2 or 4))
            throw new InvalidDataException("KTX texture has an invalid component size.");
        stream.Position = 0;
        return new BcDecoder().DecodeToImageRgba32(stream);
    }

    private static Image<Rgba32> DecodeKtx2(Stream stream)
    {
        RequireSignature(stream, Ktx2Identifier);
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        stream.Position = 12;
        var vkFormat = reader.ReadUInt32();
        var typeSize = reader.ReadUInt32();
        var width = reader.ReadUInt32();
        var height = reader.ReadUInt32();
        var depth = reader.ReadUInt32();
        var layerCount = reader.ReadUInt32();
        var faceCount = reader.ReadUInt32();
        var levelCount = reader.ReadUInt32();
        var supercompression = reader.ReadUInt32();
        ValidateDimensions(width, height);
        if (depth > 1 || layerCount > 1 || faceCount != 1)
            throw new NotSupportedException("Only 2D, single-layer KTX2 textures are supported.");
        if (levelCount > 32)
            throw new InvalidDataException("KTX2 texture has too many mip levels.");
        if (levelCount == 0)
            levelCount = 1;
        if (supercompression != 0)
            throw new NotSupportedException("Supercompressed KTX2 textures are not supported.");
        if (typeSize != 1 && vkFormat is not (131 or 132 or 133 or 134 or 135 or 136 or 137 or 138 or
                139 or 140 or 141 or 142 or 143 or 144 or 145 or 146))
            throw new NotSupportedException("This KTX2 component type is not supported.");

        stream.Position = 80;
        var levelOffset = reader.ReadUInt64();
        var levelLength = reader.ReadUInt64();
        var uncompressedLength = reader.ReadUInt64();
        if (levelLength == 0 || levelLength > int.MaxValue || levelOffset > (ulong)stream.Length ||
            levelLength > (ulong)stream.Length - levelOffset)
            throw new InvalidDataException("KTX2 level data is outside the file.");
        if (uncompressedLength != 0 && uncompressedLength != levelLength)
            throw new InvalidDataException("KTX2 level data has inconsistent lengths.");
        var compressedFormat = TryGetDxgiFormat(vkFormat, out var dxgiFormat);
        var expectedLength = compressedFormat
            ? GetBlockCompressedLength(width, height, vkFormat is 131 or 132 or 133 or 134 or 139 or 140 ? 8 : 16)
            : vkFormat is 37 or 43 or 44 or 50
                ? checked((int)((long)width * height * 4))
                : throw new NotSupportedException($"KTX2 Vulkan format {vkFormat} is not supported.");
        if (levelLength < (ulong)expectedLength)
            throw new InvalidDataException("KTX2 base image data is truncated.");

        stream.Position = (long)levelOffset;
        var levelData = reader.ReadBytes(expectedLength);
        if (levelData.Length != expectedLength)
            throw new EndOfStreamException("KTX2 level data is truncated.");

        if (compressedFormat)
        {
            using var dds = CreateDx10Dds(width, height, dxgiFormat, levelData);
            return new BcDecoder().DecodeToImageRgba32(dds);
        }

        return DecodeRgbaKtx2(width, height, vkFormat, levelData);
    }

    private static Image<Rgba32> DecodeBlp(Stream stream)
    {
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        stream.Position = 0;
        var magic = new string(reader.ReadChars(4));
        if (magic is not ("BLP1" or "BLP2"))
            throw new InvalidDataException("Invalid Blizzard texture signature.");

        var isBlp2 = magic == "BLP2";
        var encoding = reader.ReadUInt32();
        byte alphaDepth;
        byte alphaType;
        uint width;
        uint height;
        uint[] offsets;
        uint[] lengths;
        long paletteOffset;
        if (isBlp2)
        {
            if (encoding != 1)
                throw new InvalidDataException("Unsupported BLP2 version.");
            encoding = reader.ReadByte();
            alphaDepth = reader.ReadByte();
            alphaType = reader.ReadByte();
            _ = reader.ReadByte();
            width = reader.ReadUInt32();
            height = reader.ReadUInt32();
            offsets = ReadUInt32Array(reader, 16);
            lengths = ReadUInt32Array(reader, 16);
            paletteOffset = stream.Position;
        }
        else
        {
            var flags = reader.ReadUInt32();
            alphaDepth = (byte)(flags & 0xFF);
            alphaType = 0;
            width = reader.ReadUInt32();
            height = reader.ReadUInt32();
            _ = reader.ReadUInt32();
            _ = reader.ReadUInt32();
            offsets = ReadUInt32Array(reader, 16);
            lengths = ReadUInt32Array(reader, 16);
            paletteOffset = stream.Position;
        }

        ValidateDimensions(width, height);
        var pixelCount = checked((int)(width * height));
        var dataOffset = offsets[0];
        var dataLength = lengths[0];
        ValidateRegion(stream, dataOffset, dataLength);
        if (dataLength == 0)
            throw new InvalidDataException("The BLP texture has no base mip level.");

        if (encoding == 0)
            return DecodeBlpJpeg(stream, reader, paletteOffset, dataOffset, dataLength);
        if (encoding == 2 && isBlp2)
        {
            var fourCc = alphaType switch
            {
                0 => "DXT1",
                1 => "DXT3",
                7 => "DXT5",
                _ => throw new NotSupportedException($"BLP2 alpha encoding {alphaType} is not supported.")
            };
            stream.Position = dataOffset;
            var dxtLength = GetBlockCompressedLength(width, height, fourCc == "DXT1" ? 8 : 16);
            if (dataLength < dxtLength)
                throw new InvalidDataException("The BLP compressed base image is truncated.");
            var dxtData = reader.ReadBytes(dxtLength);
            using var dds = CreateDds(width, height, fourCc, dxtData);
            return new BcDecoder().DecodeToImageRgba32(dds);
        }

        stream.Position = paletteOffset;
        var palette = reader.ReadBytes(1024);
        if (palette.Length != 1024)
            throw new InvalidDataException("The BLP palette is truncated.");
        stream.Position = dataOffset;
        var pixelDataLength = encoding == 3 && isBlp2
            ? checked(pixelCount * 4)
            : GetBlpPaletteDataLength(pixelCount, alphaDepth);
        if (dataLength < pixelDataLength)
            throw new InvalidDataException("The BLP base image is truncated.");
        var pixels = reader.ReadBytes(pixelDataLength);

        if (encoding == 3 && isBlp2)
            return DecodeBlpBgra(width, height, pixels);
        if (encoding == 1)
            return DecodeBlpPalette(width, height, pixels, palette, pixelCount, alphaDepth);

        throw new NotSupportedException($"BLP color encoding {encoding} is not supported.");
    }

    private static Image<Rgba32> DecodeVtf(Stream stream)
    {
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        stream.Position = 0;
        if (new string(reader.ReadChars(4)) != "VTF\0")
            throw new InvalidDataException("Invalid Valve texture signature.");
        var major = reader.ReadUInt32();
        var minor = reader.ReadUInt32();
        var headerSize = reader.ReadUInt32();
        if (major != 7 || minor < 1 || headerSize > stream.Length || headerSize < 63)
            throw new InvalidDataException("Unsupported or invalid VTF header.");

        var width = reader.ReadUInt16();
        var height = reader.ReadUInt16();
        var flags = reader.ReadUInt32();
        var frames = reader.ReadUInt16();
        stream.Position = 52;
        var highResolutionFormat = reader.ReadUInt32();
        var mipmapCount = reader.ReadByte();
        var lowResolutionFormat = reader.ReadUInt32();
        var lowResolutionWidth = reader.ReadByte();
        var lowResolutionHeight = reader.ReadByte();
        var depth = minor >= 2 ? reader.ReadUInt16() : (ushort)1;
        ValidateDimensions(width, height);
        if (frames != 1 || depth != 1 || (flags & 0x00004000) != 0)
            throw new NotSupportedException("Animated, volumetric, and cubemap VTF textures are not supported.");
        if (mipmapCount == 0 || mipmapCount > 16)
            throw new InvalidDataException("VTF mipmap count is invalid.");

        long imageOffset = headerSize;
        if (lowResolutionFormat != uint.MaxValue && lowResolutionWidth > 0 && lowResolutionHeight > 0)
            imageOffset = checked(imageOffset + GetVtfImageLength(lowResolutionFormat, lowResolutionWidth, lowResolutionHeight));
        for (var mip = mipmapCount - 1; mip > 0; mip--)
        {
            var mipWidth = Math.Max(1, width >> mip);
            var mipHeight = Math.Max(1, height >> mip);
            imageOffset = checked(imageOffset + GetVtfImageLength(highResolutionFormat, mipWidth, mipHeight));
        }

        var imageLength = GetVtfImageLength(highResolutionFormat, width, height);
        if (imageOffset > stream.Length || imageLength > stream.Length - imageOffset)
            throw new InvalidDataException("VTF image data is truncated.");
        stream.Position = imageOffset;
        if (highResolutionFormat is 13 or 14 or 15 or 20)
        {
            var fourCc = highResolutionFormat switch
            {
                13 or 20 => "DXT1",
                14 => "DXT3",
                _ => "DXT5"
            };
            var compressed = reader.ReadBytes(imageLength);
            using var dds = CreateDds(width, height, fourCc, compressed);
            return new BcDecoder().DecodeToImageRgba32(dds);
        }

        var data = reader.ReadBytes(imageLength);
        if (data.Length != imageLength)
            throw new EndOfStreamException("VTF image data is truncated.");
        return DecodeVtfPixels(width, height, highResolutionFormat, data);
    }

    private static Image<Rgba32> DecodeTxd(Stream stream)
    {
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        stream.Position = 0;
        if (stream.Length < 12 || reader.ReadUInt32() != 0x16)
            throw new InvalidDataException("Invalid RenderWare texture dictionary.");
        var rootLength = reader.ReadUInt32();
        _ = reader.ReadUInt32();
        var rootEnd = checked(stream.Position + rootLength);
        if (rootEnd > stream.Length)
            throw new InvalidDataException("RenderWare texture dictionary is truncated.");
        return FindTxdTexture(stream, reader, stream.Position, rootEnd, 0) ??
            throw new InvalidDataException("The texture dictionary contains no supported PC DXT texture.");
    }

    private static Image<Rgba32>? FindTxdTexture(Stream stream, BinaryReader reader, long start, long end, int depth)
    {
        if (depth > 16)
            throw new InvalidDataException("RenderWare texture dictionary nesting is too deep.");

        var chunkCount = 0;
        stream.Position = start;
        while (stream.Position < end)
        {
            if (++chunkCount > 100_000 || end - stream.Position < 12)
                throw new InvalidDataException("RenderWare texture dictionary has an invalid chunk table.");
            var type = reader.ReadUInt32();
            var length = reader.ReadUInt32();
            _ = reader.ReadUInt32();
            var payloadStart = stream.Position;
            var payloadEnd = checked(payloadStart + length);
            if (payloadEnd > end)
                throw new InvalidDataException("RenderWare chunk data is outside its parent.");

            if (type == 0x15)
            {
                var image = DecodeTxdNativeTexture(stream, reader, payloadStart, payloadEnd);
                if (image is not null)
                    return image;
            }
            else if (type == 0x16)
            {
                var image = FindTxdTexture(stream, reader, payloadStart, payloadEnd, depth + 1);
                if (image is not null)
                    return image;
            }
            stream.Position = payloadEnd;
        }
        return null;
    }

    private static Image<Rgba32>? DecodeTxdNativeTexture(Stream stream, BinaryReader reader, long start, long end)
    {
        if (end - start < 12)
            throw new InvalidDataException("RenderWare native texture header is truncated.");
        stream.Position = start;
        if (reader.ReadUInt32() != 0x01)
            throw new InvalidDataException("RenderWare native texture has no struct chunk.");
        var structLength = reader.ReadUInt32();
        _ = reader.ReadUInt32();
        var dataStart = stream.Position;
        var structEnd = checked(dataStart + structLength);
        if (structEnd > end || structLength < 92)
            throw new InvalidDataException("RenderWare native texture struct is truncated.");

        stream.Position = dataStart;
        var platform = reader.ReadUInt32();
        if (platform is not (8 or 9))
            return null;
        stream.Position = dataStart + 72;
        _ = reader.ReadUInt32();
        var d3dFormat = reader.ReadUInt32();
        var width = reader.ReadUInt16();
        var height = reader.ReadUInt16();
        _ = reader.ReadByte();
        _ = reader.ReadByte();
        _ = reader.ReadByte();
        var compression = reader.ReadByte();
        ValidateDimensions(width, height);

        var fourCc = d3dFormat switch
        {
            0x31545844 => "DXT1",
            0x33545844 => "DXT3",
            0x35545844 => "DXT5",
            _ => compression switch
            {
                1 => "DXT1",
                3 => "DXT3",
                5 => "DXT5",
                _ => null
            }
        };
        if (fourCc is null)
            return null;

        stream.Position = dataStart + 88;
        var rasterLength = reader.ReadUInt32();
        var rasterStart = stream.Position;
        var dxtLength = GetBlockCompressedLength(width, height, fourCc == "DXT1" ? 8 : 16);
        if (rasterLength < dxtLength || rasterStart + rasterLength > structEnd)
            throw new InvalidDataException("RenderWare compressed texture data is truncated.");
        var pixels = reader.ReadBytes(dxtLength);
        if (pixels.Length != dxtLength)
            throw new EndOfStreamException("RenderWare compressed texture data is truncated.");
        using var dds = CreateDds(width, height, fourCc, pixels);
        return new BcDecoder().DecodeToImageRgba32(dds);
    }

    private static int GetVtfImageLength(uint format, int width, int height)
    {
        var pixelCount = checked((long)width * height);
        var bytes = format switch
        {
            13 or 20 => checked((long)Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * 8),
            14 or 15 => checked((long)Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * 16),
            0 or 12 or 16 => pixelCount * 4,
            2 or 3 => pixelCount * 3,
            _ => throw new NotSupportedException($"VTF pixel format {format} is not supported.")
        };
        if (bytes > int.MaxValue)
            throw new InvalidDataException("VTF image data is too large.");
        return (int)bytes;
    }

    private static int GetBlockCompressedLength(uint width, uint height, int bytesPerBlock)
    {
        var blockWidth = Math.Max(1L, ((long)width + 3) / 4);
        var blockHeight = Math.Max(1L, ((long)height + 3) / 4);
        return checked((int)(blockWidth * blockHeight * bytesPerBlock));
    }

    private static Image<Rgba32> DecodeVtfPixels(ushort width, ushort height, uint format, byte[] data)
    {
        var image = new Image<Rgba32>(width, height);
        var cursor = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                byte red;
                byte green;
                byte blue;
                byte alpha;
                switch (format)
                {
                    case 0:
                        red = data[cursor++];
                        green = data[cursor++];
                        blue = data[cursor++];
                        alpha = data[cursor++];
                        break;
                    case 12:
                        blue = data[cursor++];
                        green = data[cursor++];
                        red = data[cursor++];
                        alpha = data[cursor++];
                        break;
                    case 16:
                        blue = data[cursor++];
                        green = data[cursor++];
                        red = data[cursor++];
                        cursor++;
                        alpha = 255;
                        break;
                    case 2:
                        red = data[cursor++];
                        green = data[cursor++];
                        blue = data[cursor++];
                        alpha = 255;
                        break;
                    case 3:
                        blue = data[cursor++];
                        green = data[cursor++];
                        red = data[cursor++];
                        alpha = 255;
                        break;
                    default:
                        throw new NotSupportedException($"VTF pixel format {format} is not supported.");
                }
                image[x, y] = new Rgba32(red, green, blue, alpha);
            }
        }
        return image;
    }

    private static int GetBlpPaletteDataLength(int pixelCount, byte alphaDepth)
    {
        var alphaLength = alphaDepth switch
        {
            0 => 0,
            1 => (pixelCount + 7) / 8,
            4 => (pixelCount + 1) / 2,
            8 => pixelCount,
            _ => throw new NotSupportedException($"BLP alpha depth {alphaDepth} is not supported.")
        };
        return checked(pixelCount + alphaLength);
    }

    private static Image<Rgba32> DecodeBlpJpeg(
        Stream stream, BinaryReader reader, long headerOffset, uint dataOffset, uint dataLength)
    {
        stream.Position = headerOffset;
        var headerLength = reader.ReadUInt32();
        if (headerLength > 1020)
            throw new InvalidDataException("The BLP JPEG header is too large.");
        var header = reader.ReadBytes((int)headerLength);
        if (header.Length != (int)headerLength)
            throw new InvalidDataException("The BLP JPEG header is truncated.");

        stream.Position = dataOffset;
        var imageData = reader.ReadBytes((int)dataLength);
        if (imageData.Length != (int)dataLength)
            throw new InvalidDataException("The BLP JPEG image is truncated.");
        using var jpeg = new MemoryStream(header.Length + imageData.Length);
        jpeg.Write(header);
        jpeg.Write(imageData);
        jpeg.Position = 0;
        return Image.Load<Rgba32>(jpeg);
    }

    private static Image<Rgba32> DecodeBlpBgra(uint width, uint height, byte[] pixels)
    {
        var pixelCount = checked((int)(width * height));
        if (pixels.Length < pixelCount * 4L)
            throw new InvalidDataException("The BLP BGRA image is truncated.");
        var image = new Image<Rgba32>((int)width, (int)height);
        for (var index = 0; index < pixelCount; index++)
        {
            var offset = index * 4;
            image[index % (int)width, index / (int)width] =
                new Rgba32(pixels[offset + 2], pixels[offset + 1], pixels[offset], pixels[offset + 3]);
        }
        return image;
    }

    private static Image<Rgba32> DecodeBlpPalette(
        uint width, uint height, byte[] mipData, byte[] palette, int pixelCount, byte alphaDepth)
    {
        if (mipData.Length < pixelCount)
            throw new InvalidDataException("The BLP indexed image is truncated.");
        var alphaLength = alphaDepth switch
        {
            0 => 0,
            1 => (pixelCount + 7) / 8,
            4 => (pixelCount + 1) / 2,
            8 => pixelCount,
            _ => throw new NotSupportedException($"BLP alpha depth {alphaDepth} is not supported.")
        };
        if (mipData.Length - pixelCount < alphaLength)
            throw new InvalidDataException("The BLP alpha data is truncated.");

        var image = new Image<Rgba32>((int)width, (int)height);
        for (var index = 0; index < pixelCount; index++)
        {
            var paletteOffset = mipData[index] * 4;
            byte alpha = alphaDepth switch
            {
                0 => (byte)255,
                1 => (byte)(((mipData[pixelCount + index / 8] >> (index & 7)) & 1) * 255),
                4 => (byte)(((mipData[pixelCount + index / 2] >> ((index & 1) * 4)) & 0xF) * 17),
                8 => mipData[pixelCount + index],
                _ => (byte)255
            };
            image[index % (int)width, index / (int)width] =
                new Rgba32(palette[paletteOffset + 2], palette[paletteOffset + 1], palette[paletteOffset], alpha);
        }
        return image;
    }

    private static Image<Rgba32> DecodeRgbaKtx2(uint width, uint height, uint vkFormat, byte[] data)
    {
        var pixelCount = checked((int)(width * height));
        if (data.Length < pixelCount * 4L)
            throw new InvalidDataException("The KTX2 RGBA image is truncated.");
        var bgra = vkFormat is 44 or 50;
        var image = new Image<Rgba32>((int)width, (int)height);
        for (var index = 0; index < pixelCount; index++)
        {
            var offset = index * 4;
            image[index % (int)width, index / (int)width] = bgra
                ? new Rgba32(data[offset + 2], data[offset + 1], data[offset], data[offset + 3])
                : new Rgba32(data[offset], data[offset + 1], data[offset + 2], data[offset + 3]);
        }
        return image;
    }

    private static MemoryStream CreateDds(uint width, uint height, string fourCc, byte[] data)
    {
        var header = new MemoryStream();
        using (var writer = new BinaryWriter(header, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            writer.Write("DDS "u8);
            WriteDdsHeader(writer, width, height, (uint)data.Length, fourCc);
        }
        header.Write(data);
        header.Position = 0;
        return header;
    }

    private static MemoryStream CreateDx10Dds(uint width, uint height, uint dxgiFormat, byte[] data)
    {
        var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            writer.Write("DDS "u8);
            WriteDdsHeader(writer, width, height, (uint)data.Length, "DX10");
            writer.Write(dxgiFormat);
            writer.Write(3u);
            writer.Write(0u);
            writer.Write(1u);
            writer.Write(0u);
        }
        stream.Write(data);
        stream.Position = 0;
        return stream;
    }

    private static void WriteDdsHeader(BinaryWriter writer, uint width, uint height, uint dataLength, string fourCc)
    {
        writer.Write(124u);
        writer.Write(0x00081007u);
        writer.Write(height);
        writer.Write(width);
        writer.Write(dataLength);
        writer.Write(0u);
        writer.Write(0u);
        for (var index = 0; index < 11; index++)
            writer.Write(0u);
        writer.Write(32u);
        writer.Write(4u);
        writer.Write(System.Text.Encoding.ASCII.GetBytes(fourCc));
        writer.Write(0u);
        for (var index = 0; index < 4; index++)
            writer.Write(0u);
        writer.Write(0x1000u);
        for (var index = 0; index < 4; index++)
            writer.Write(0u);
    }

    private static bool TryGetDxgiFormat(uint vkFormat, out uint dxgiFormat)
    {
        dxgiFormat = vkFormat switch
        {
            131 or 133 => 71,
            132 or 134 => 72,
            135 => 74,
            136 => 75,
            137 => 77,
            138 => 78,
            139 => 80,
            140 => 81,
            141 => 83,
            142 => 84,
            143 => 95,
            144 => 96,
            145 => 98,
            146 => 99,
            _ => 0
        };
        return dxgiFormat != 0;
    }

    private static uint[] ReadUInt32Array(BinaryReader reader, int count)
    {
        var values = new uint[count];
        for (var index = 0; index < count; index++)
            values[index] = reader.ReadUInt32();
        return values;
    }

    private static void ValidateRegion(Stream stream, uint offset, uint length)
    {
        if (offset > stream.Length || length > stream.Length - offset || length > int.MaxValue)
            throw new InvalidDataException("Texture data is outside the file.");
    }

    private static void ValidateDimensions(uint width, uint height)
    {
        if (width == 0 || height == 0 || width > MaximumDimension || height > MaximumDimension ||
            (long)width * height > MaximumPixelCount)
            throw new InvalidDataException("Image exceeds the preview size limit.");
    }

    private static void ValidateDimensions(int width, int height) =>
        ValidateDimensions(checked((uint)width), checked((uint)height));

    private static void RequireSignature(Stream stream, ReadOnlySpan<byte> expected)
    {
        if (stream.Length < expected.Length)
            throw new InvalidDataException("Texture header is truncated.");
        Span<byte> actual = stackalloc byte[12];
        stream.Position = 0;
        if (stream.Read(actual[..expected.Length]) != expected.Length || !actual[..expected.Length].SequenceEqual(expected))
            throw new InvalidDataException("Texture signature is invalid.");
    }

    private static Stream EnsureSeekable(Stream source)
    {
        if (source.CanSeek)
            return source;
        var copy = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (copy.Length + read > MaximumInputBytes)
            {
                copy.Dispose();
                throw new InvalidDataException("Texture file exceeds the 128 MB decode limit.");
            }
            copy.Write(buffer, 0, read);
        }
        copy.Position = 0;
        return copy;
    }
}
