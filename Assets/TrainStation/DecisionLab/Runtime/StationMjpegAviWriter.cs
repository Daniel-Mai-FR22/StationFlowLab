using System;
using System.IO;
using System.Text;

namespace StationFlow.TrainStation.DecisionLab
{
    /// <summary>Streaming AVI 1.0/MJPEG encoder container. JPEG bytes are encoded separately, never retained here.</summary>
    public sealed class StationMjpegAviWriter : IDisposable
    {
        readonly FileStream stream, index;
        readonly BinaryWriter writer, indexWriter;
        readonly string indexPath;
        readonly long riffSizeAt, mainFlagsAt, mainFramesAt, streamFramesAt, movieSizeAt, movieTypeAt;
        int largestFrame;
        bool disposed;
        public int FrameCount { get; private set; }
        public long Length => stream.Length;

        public StationMjpegAviWriter(string path, int width, int height, int framesPerSecond)
        {
            if (width < 1 || width > short.MaxValue || height < 1 || height > short.MaxValue || framesPerSecond < 1 || framesPerSecond > 60)
                throw new ArgumentOutOfRangeException(nameof(width), "AVI dimensions or frame rate are outside supported bounds.");
            // CreateNew prevents a second session from replacing existing evidence.
            stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 65536);
            writer = new BinaryWriter(stream, Encoding.ASCII, true);
            indexPath = path + ".index.tmp";
            try
            {
                index = new FileStream(indexPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 4096);
                indexWriter = new BinaryWriter(index, Encoding.ASCII, true);
                FourCC("RIFF"); riffSizeAt = stream.Position; writer.Write(0u); FourCC("AVI ");
                FourCC("LIST"); long headerSizeAt = stream.Position; writer.Write(0u); long headerStart = stream.Position; FourCC("hdrl");
                FourCC("avih"); writer.Write(56u); writer.Write((uint)(1000000 / framesPerSecond)); writer.Write(0u); writer.Write(0u);
                mainFlagsAt = stream.Position; writer.Write(0u); mainFramesAt = stream.Position; writer.Write(0u);
                writer.Write(0u); writer.Write(1u); writer.Write(0u); writer.Write((uint)width); writer.Write((uint)height);
                for (int i = 0; i < 4; ++i) writer.Write(0u);
                FourCC("LIST"); long streamSizeAt = stream.Position; writer.Write(0u); long streamStart = stream.Position; FourCC("strl");
                FourCC("strh"); writer.Write(56u); FourCC("vids"); FourCC("MJPG"); writer.Write(0u); writer.Write((ushort)0); writer.Write((ushort)0);
                writer.Write(0u); writer.Write(1u); writer.Write((uint)framesPerSecond); writer.Write(0u);
                streamFramesAt = stream.Position; writer.Write(0u); writer.Write(0u); writer.Write(uint.MaxValue); writer.Write(0u);
                writer.Write((short)0); writer.Write((short)0); writer.Write((short)width); writer.Write((short)height);
                FourCC("strf"); writer.Write(40u); writer.Write(40u); writer.Write(width); writer.Write(height);
                writer.Write((ushort)1); writer.Write((ushort)24); FourCC("MJPG"); writer.Write(checked((uint)(width * height * 3)));
                writer.Write(0); writer.Write(0); writer.Write(0u); writer.Write(0u);
                Patch(streamSizeAt, checked((uint)(stream.Position - streamStart)));
                Patch(headerSizeAt, checked((uint)(stream.Position - headerStart)));
                FourCC("LIST"); movieSizeAt = stream.Position; writer.Write(4u); movieTypeAt = stream.Position; FourCC("movi");
                Checkpoint();
            }
            catch { indexWriter?.Dispose(); index?.Dispose(); writer.Dispose(); stream.Dispose(); throw; }
        }

        public int AppendJpeg(byte[] jpeg)
        {
            if (disposed) throw new ObjectDisposedException(nameof(StationMjpegAviWriter));
            if (jpeg == null || jpeg.Length < 4 || jpeg[0] != 0xff || jpeg[1] != 0xd8 || jpeg[jpeg.Length-2] != 0xff || jpeg[jpeg.Length-1] != 0xd9)
                throw new ArgumentException("A complete JPEG image is required.", nameof(jpeg));
            if (stream.Length + jpeg.Length + 32L > 1000000000L) throw new IOException("AVI segment size limit reached; start another segment.");
            long position = stream.Position;
            FourCC("00dc"); writer.Write((uint)jpeg.Length); writer.Write(jpeg);
            if ((jpeg.Length & 1) != 0) writer.Write((byte)0);
            indexWriter.Write(Encoding.ASCII.GetBytes("00dc")); indexWriter.Write(0x10u);
            indexWriter.Write(checked((uint)(position - movieTypeAt))); indexWriter.Write((uint)jpeg.Length);
            largestFrame = Math.Max(largestFrame, jpeg.Length);
            int frame = FrameCount++;
            // A readable, unindexed prefix survives an ordinary interruption between completed writes.
            // The journal permits diagnosis of an unfinalized segment after an abrupt process termination.
            if (FrameCount % 5 == 0) Checkpoint();
            return frame;
        }

        public void Checkpoint()
        {
            if (disposed) return;
            Patch(movieSizeAt, checked((uint)(stream.Position - movieTypeAt)));
            Patch(mainFramesAt, (uint)FrameCount); Patch(streamFramesAt, (uint)FrameCount);
            Patch(riffSizeAt, checked((uint)(stream.Length - 8)));
            writer.Flush(); stream.Flush(); indexWriter.Flush(); index.Flush();
        }

        public void Dispose()
        {
            if (disposed) return;
            try
            {
                Checkpoint();
                FourCC("idx1"); writer.Write(checked((uint)(FrameCount * 16L)));
                indexWriter.Flush(); index.Position = 0; index.CopyTo(stream, 65536);
                Patch(mainFlagsAt, 0x10u); Patch(riffSizeAt, checked((uint)(stream.Length - 8)));
                writer.Flush(); stream.Flush();
            }
            finally
            {
                disposed = true; indexWriter.Dispose(); index.Dispose(); writer.Dispose(); stream.Dispose();
            }
            File.Delete(indexPath);
        }

        void FourCC(string value) => writer.Write(Encoding.ASCII.GetBytes(value));
        void Patch(long at, uint value) { long restore = stream.Position; stream.Position = at; writer.Write(value); stream.Position = restore; }
    }
}
