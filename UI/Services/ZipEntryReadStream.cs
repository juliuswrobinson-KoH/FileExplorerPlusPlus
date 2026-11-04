using System.IO.Compression;

namespace UI.Services;

public sealed class ZipEntryReadStream : Stream
{
    private readonly ZipArchiveEntry _entry;
    private Stream? _entryStream;
    private long _position;
    private long _streamPosition;
    private bool _disposed;

    public ZipEntryReadStream(ZipArchiveEntry entry)
    {
        _entry = entry;
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => false;
    public override long Length => _entry.Length;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(_position + offset),
            SeekOrigin.End => checked(Length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        if (target < 0)
            throw new IOException("Cannot seek before the start of the archive entry.");

        _position = target;
        return _position;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_position >= Length || buffer.Length == 0)
            return 0;

        EnsureStreamAtPosition();
        await SkipToPositionAsync(cancellationToken);
        var count = (int)Math.Min(buffer.Length, Length - _position);
        var read = await _entryStream!.ReadAsync(buffer[..count], cancellationToken);
        _position += read;
        _streamPosition += read;
        return read;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override int Read(Span<byte> buffer)
    {
        var temp = new byte[buffer.Length];
        var read = Read(temp, 0, temp.Length);
        temp.AsSpan(0, read).CopyTo(buffer);
        return read;
    }

    private void EnsureStreamAtPosition()
    {
        if (_entryStream is not null && _position >= _streamPosition)
            return;
        _entryStream?.Dispose();
        _entryStream = _entry.Open();
        _streamPosition = 0;
    }

    private async Task SkipToPositionAsync(CancellationToken cancellationToken)
    {
        var skipBuffer = new byte[8192];
        while (_streamPosition < _position)
        {
            var count = (int)Math.Min(skipBuffer.Length, _position - _streamPosition);
            var read = await _entryStream!.ReadAsync(skipBuffer.AsMemory(0, count), cancellationToken);
            if (read == 0)
                throw new EndOfStreamException("The archive entry ended before the requested offset.");
            _streamPosition += read;
        }
    }

    public override void Flush() => throw new NotSupportedException("Archive entries are read-only.");
    public override void SetLength(long value) => throw new NotSupportedException("Archive entries are read-only.");
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("Archive entries are read-only.");

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _disposed = true;
            _entryStream?.Dispose();
            _entryStream = null;
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            if (_entryStream is not null)
                await _entryStream.DisposeAsync();
            _entryStream = null;
        }
        GC.SuppressFinalize(this);
    }
}
