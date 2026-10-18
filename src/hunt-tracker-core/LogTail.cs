using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using System.Text;

namespace HuntTracker
{
    public sealed class LogTail
    {
        private const int ReadLimit = 1048576;
        private const int IdleVerificationMs = 1000;
        private static readonly string[] Empty = new string[0];
        private readonly byte[] buffer = new byte[8192];
        private readonly char[] chars = new char[8193];
        private readonly byte[] anchor = new byte[64];
        private readonly byte[] check = new byte[64];
        private readonly Decoder decoder = Encoding.UTF8.GetDecoder();
        private int anchorLength;
        private StringBuilder remainder = new();
        private bool initialized, skipOnFirstRead, discardingLine;
        private DateTime created = DateTime.MinValue;
        private readonly FileInfo info;
        private DateTime lastWrite;
        private long nextVerification;

        public string Path { get; private set; }
        public long Position { get; private set; }
        public int Generation { get; private set; }
        public bool Healthy { get; private set; }
        public bool CaughtUp { get; private set; }

        public LogTail(string path, bool skipExisting)
        {
            Path = path;
            info = new FileInfo(path);
            CaughtUp = true;
            if (!skipExisting || !File.Exists(path))
                return;
            skipOnFirstRead = true;
            try
            {
                using (FileStream stream = Open())
                {
                    created = File.GetCreationTimeUtc(Path);
                    Position = stream.Length;
                    RememberAnchor(stream);
                    initialized = true;
                    skipOnFirstRead = false;
                    Healthy = true;
                }
            }
            catch (IOException) { Healthy = false; }
            catch (UnauthorizedAccessException) { Healthy = false; }
            catch (SecurityException) { Healthy = false; }
        }

        private FileStream Open()
        {
            return new FileStream(Path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 1);
        }

        private static int ReadBytes(FileStream stream, byte[] target, int count)
        {
            int total = 0;
            while (total < count)
            {
                int read = stream.Read(target, total, count - total);
                if (read == 0)
                    break;
                total += read;
            }
            return total;
        }

        private bool MatchesAnchor(FileStream stream)
        {
            stream.Position = Position - anchorLength;
            if (ReadBytes(stream, check, anchorLength) != anchorLength)
                return false;
            for (int i = 0; i < anchorLength; i++)
                if (check[i] != anchor[i])
                    return false;
            return true;
        }

        private void RememberAnchor(FileStream stream)
        {
            anchorLength = (int)Math.Min(anchor.Length, Position);
            stream.Position = Position - anchorLength;
            if (ReadBytes(stream, anchor, anchorLength) != anchorLength)
                throw new IOException("Log changed during a read.");
        }

        private void ClearLine()
        {
            if (remainder.Capacity > buffer.Length)
                remainder = new();
            else
                remainder.Clear();
        }

        public string[] Read(bool verifyUnchanged = true)
        {
            List<string>? lines = null;
            try
            {
                info.Refresh();
                if (!info.Exists)
                {
                    Healthy = false;
                    return Empty;
                }
                var now = Environment.TickCount64;
                if (!verifyUnchanged && Healthy && CaughtUp && initialized && now < nextVerification &&
                    info.Length == Position && info.CreationTimeUtc == created && info.LastWriteTimeUtc == lastWrite)
                    return Empty;
                lastWrite = info.LastWriteTimeUtc;
                nextVerification = now + IdleVerificationMs;
                using (FileStream stream = Open())
                {
                    DateTime currentCreated = File.GetCreationTimeUtc(Path);
                    long length = stream.Length;
                    bool reset = initialized && (currentCreated != created || length < Position || !MatchesAnchor(stream));
                    if (!initialized)
                    {
                        created = currentCreated;
                        initialized = true;
                        if (skipOnFirstRead)
                        {
                            Position = length;
                            RememberAnchor(stream);
                            skipOnFirstRead = false;
                            Healthy = CaughtUp = true;
                            return Empty;
                        }
                    }
                    if (!reset && length == Position)
                    {
                        Healthy = CaughtUp = true;
                        return Empty;
                    }
                    if (reset)
                    {
                        Position = 0;
                        ClearLine();
                        discardingLine = false;
                        decoder.Reset();
                        created = currentCreated;
                        Generation++;
                    }
                    stream.Position = Position;
                    long limit = Math.Min(stream.Length, Position + ReadLimit);
                    lines = new List<string>();
                    while (stream.Position < limit)
                    {
                        int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, limit - stream.Position));
                        if (read == 0)
                            break;
                        int count = decoder.GetChars(buffer, 0, read, chars, 0, false);
                        var text = chars.AsSpan(0, count);
                        while (!text.IsEmpty)
                        {
                            var end = text.IndexOf('\n');
                            var segment = end < 0 ? text : text[..end];
                            if (!discardingLine)
                            {
                                if (remainder.Length + segment.Length > ReadLimit)
                                {
                                    ClearLine();
                                    discardingLine = true;
                                }
                                else
                                    remainder.Append(segment);
                            }
                            if (end < 0)
                                break;
                            if (!discardingLine)
                            {
                                if (remainder.Length > 0 && remainder[^1] == '\r')
                                    remainder.Length--;
                                lines.Add(remainder.ToString().TrimStart('\uFEFF'));
                            }
                            ClearLine();
                            discardingLine = false;
                            text = text[(end + 1)..];
                        }
                        Position = stream.Position;
                    }
                    CaughtUp = Position >= stream.Length;
                    RememberAnchor(stream);
                    initialized = true;
                    skipOnFirstRead = false;
                    Healthy = true;
                }
            }
            catch (IOException) { Healthy = false; }
            catch (UnauthorizedAccessException) { Healthy = false; }
            catch (SecurityException) { Healthy = false; }
            return lines == null ? Empty : lines.ToArray();
        }
    }
}
