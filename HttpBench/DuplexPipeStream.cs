using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace HttpBench
{
    /// <summary>
    /// Wraps a duplex pipe as a Stream
    /// </summary>
    public sealed class DuplexPipeStream : Stream, IDuplexPipe
    {
        readonly PipeReader _reader;
        readonly PipeWriter _writer;
        readonly bool _completeOnClose;

        public PipeReader Input => _reader;
        public PipeWriter Output => _writer;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotImplementedException();

        public override long Position { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

        public DuplexPipeStream(IDuplexPipe duplexPipe, bool completeOnClose = true)
            : this(duplexPipe.Input, duplexPipe.Output, completeOnClose)
        {
        }

        public DuplexPipeStream(PipeReader reader, PipeWriter writer, bool completeOnClose = true)
        {
            _reader = reader;
            _writer = writer;
            _completeOnClose = completeOnClose;
        }

        /// <summary>
        /// Creates a client/server pair of streams that operates in-memory.
        /// </summary>
        /// <param name="completeOnClose">If true, disposal of a stream should be observed by the other stream as equivalent to seeing shutdown(SD_BOTH).</param>
        /// <returns></returns>
        public static (DuplexPipeStream, DuplexPipeStream) CreateInMemoryPair(bool completeOnClose = true)
        {
            Pipe firstBuffer = new Pipe();
            Pipe secondBuffer = new Pipe();

            var first = new DuplexPipeStream(firstBuffer.Reader, secondBuffer.Writer, completeOnClose);
            var second = new DuplexPipeStream(secondBuffer.Reader, firstBuffer.Writer, completeOnClose);

            return (first, second);
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotImplementedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotImplementedException();
        }

        public override void SetLength(long value)
        {
            throw new NotImplementedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotImplementedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadResult res = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (res.IsCanceled) throw new TaskCanceledException(null, null, cancellationToken);

            ReadOnlySequence<byte> sequence = res.Buffer;
            int readLen = (int)Math.Min(buffer.Length, sequence.Length);

            if (readLen != 0)
            {
                sequence = sequence.Slice(sequence.Start, readLen);
                sequence.CopyTo(buffer.Span);
                _reader.AdvanceTo(sequence.End);
            }

            return readLen;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            FlushResult res = await _writer.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (res.IsCanceled) throw new TaskCanceledException(null, null, cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            return _reader.CopyToAsync(destination, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _completeOnClose)
            {
                _reader.Complete();
                _writer.Complete();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (_completeOnClose)
            {
                await _reader.CompleteAsync().ConfigureAwait(false);
                await _writer.CompleteAsync().ConfigureAwait(false);
            }
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
