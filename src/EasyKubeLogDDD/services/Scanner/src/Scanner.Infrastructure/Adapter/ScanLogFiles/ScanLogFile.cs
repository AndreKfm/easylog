﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using Scanner.Domain.Entities;
using Scanner.Domain.Events;
using Scanner.Domain.Ports;
using Scanner.Domain.Ports.Query;
using Scanner.Domain.Shared;


namespace Scanner.Infrastructure.Adapter.ScanLogFiles
{
    public class ScanLogFile : IScanLogFile
    {
        private readonly AutoCurrentFileList _fileList;

        public ScanLogFile(IEventProducer eventProducer)
        {
            _fileList = new AutoCurrentFileList(eventProducer);
        }

        public void ScanLogFiles(ReadOnlyCollection<FileEntry> fileChanges)
        {
            _fileList.HandleFileChanges(fileChanges);
        }

        public IReadOnlyCollection<(string name, IReadOnlyCollection<LogEntry> logEntries)> GetChanges()
        {
            throw new NotImplementedException();
        }
    }

    public class AutoCurrentFileList
    {
        private readonly IEventProducer _producer;

        public AutoCurrentFileList(IEventProducer producer)
        {
            _producer = producer;
        }


        public void HandleFileChanges(ReadOnlyCollection<FileEntry> fileChanges)
        {

            try
            {
                foreach (var entry in fileChanges)
                {
                    switch (entry.ChangeType)
                    {
                        case FileSystemWatcherChangeType.Created:
                            {
                                AddFile(entry.FileName);
                                break;
                            }
                        case FileSystemWatcherChangeType.Deleted:
                            {
                                RemoveFile(entry.FileName);
                                break;
                            }
                        case FileSystemWatcherChangeType.Changed:
                            {
                                FileChanged(entry.FileName);
                                break;
                            }
                    }
                }
            }
            catch (Exception e)
            {
                Trace.TraceError($"Error reading channel in AutoCurrentFileList.ReadChannel: [{e.GetType()}] [{e.Message}]");
            }
        }

        const int MaxContentLengthToForwardForEachScanInBytes = 65536;

        private void FileChanged(string entryFileName)
        {
            _producer.PostEvent(new CheckLogFileStart(entryFileName));

            var list = _fileList;
            list.TryGetValue(entryFileName, out CurrentFileEntry? value);

            if (value == null)
            {
                AddFile(entryFileName);
                list.TryGetValue(entryFileName, out value);
            }

            int maxLoop = 1000; // Just to play it safe if something severly gone wrong -> thousand read calls should be enough 

            if (value != null)
            {
                for (; ; )
                {

                    (string content, ReadLine sizeExceeded)
                        = value.CurrentFile.ReadLineFromCurrentPositionToEnd(MaxContentLengthToForwardForEachScanInBytes);

                    Console.WriteLine($"#### ENTRY: {content}");
                    if (sizeExceeded == ReadLine.BufferSufficient || (--maxLoop <= 0))
                        break;
                }
            }
            _producer.PostEvent(new CheckLogFileCompleted(entryFileName));

        }

        private void RemoveFile(string entryFileName)
        {
            _fileList.TryGetValue(entryFileName, out CurrentFileEntry? value);
            if (value != null)
            {
                _fileList = _fileList.Remove(entryFileName);
                value.CurrentFile.Dispose();
            }
        }

        private void AddFile(string entryFileName)
        {
            var fileStream = new FileReadOnlyWrapper(entryFileName);
            _fileList = _fileList.Add(entryFileName, new CurrentFileEntry(entryFileName, fileStream));
        }


        private ImmutableDictionary<string, CurrentFileEntry> _fileList = ImmutableDictionary<string, CurrentFileEntry>.Empty;
    }


    public enum ReadLine
    {
        BufferSufficient,
        ReadLineContentExceedsSize // Will be returned if the internal buffer was too small to read all data
    }
    public interface IFile : IDisposable
    {
        (string line, ReadLine sizeExceeded)
            ReadLineFromCurrentPositionToEnd(long maxStringSize = 6000); // Read all data as string from current position to the last occurrence
        // of '\n'. If '\n' is not found the whole string will be returned if maxStringSize
        // has been reached - otherwise an empty string will be returned and more data
        // on the next call if '\n' is found
    }

    public class CurrentFileEntry
    {

        public CurrentFileEntry(string fileName, IFile fileStream)
        {
            FileName = fileName;
            CurrentFile = fileStream;
        }
        public string FileName { get; } // Current file name

        public IFile CurrentFile { get; } // Current file name
    }
    public interface IGetFile
    {
        IFile GetFile(string fileName);
    }


    public class GetFileWrapper : IGetFile
    {
        public IFile GetFile(string fileName)
        {
            return new FileReadOnlyWrapper(fileName);
        }
    }

    public interface IFileSeeker
    {
        // ReSharper disable once UnusedMemberInSuper.Global
        // ReSharper disable once UnusedMemberInSuper.Global
        bool SeekLastLineFromCurrentAndPositionOnStartOfIt(IFileStream stream);
    }

    public class FileSeeker : IFileSeeker
    {
        private byte[]? _buffer;

        private bool SeekNextLineFeedInNegativeDirectionAndPositionStreamOnIt(IFileStream stream, int steps) //, bool skipNearbyCRLF = true)
        {
            if (_buffer == null || (_buffer.Length != steps)) _buffer = new byte[steps];
            Span<byte> buffer = _buffer.AsSpan();
            var initial = stream.Position;


            for (; ; )
            {
                var current = stream.Position;
                if (current == 0)
                {
                    break;
                }
                int toRead = steps;
                if (toRead > current)
                {
                    toRead = (int)current;
                    buffer = buffer.Slice(0, toRead);
                }
                SetPositionRelative(stream, -toRead);
                int size = stream.Read(buffer);
                if (size != toRead)
                {
                    // That shouldn't happen ???
                    break;
                }

                int index = buffer.LastIndexOf((byte)'\n');
                if (index >= 0)
                {
                    var newPos = toRead - index;
                    SetPositionRelative(stream, -newPos);
                    return true;
                }
                SetPositionRelative(stream, -toRead); // Continue with next characters
            }

            SetPosition(stream, initial);
            return false;

        }

        // ReSharper disable once UnusedMethodReturnValue.Local
        private bool SetPositionRelative(IFileStream stream, long offset)
        {
            var current = stream.Position;

            var newPos = stream.Seek(offset, SeekOrigin.Current);


            Debug.Assert((newPos - current) == offset);
            return (newPos - current) == offset; // We assume that we won't position more than Int32.Max
        }

        private void SetPosition(IFileStream stream, long position)
        {
            stream.Seek(position, SeekOrigin.Begin);
        }

        public bool SeekLastLineFromCurrentAndPositionOnStartOfIt(IFileStream stream)
        {
            int steps = 80;

            var pos1 = stream.Position;

            var found1 = SeekNextLineFeedInNegativeDirectionAndPositionStreamOnIt(stream, steps);
            if (found1 == false)
            {
                if (pos1 == 0)
                    return false; // We cannot differentiate between String.Empty nothing found and String.Empty = empty log 
                                  // (though by definition right now a log is not empty) but to prevent errors just return null == nothing found
                return false; // No line feed found - so no line yet
            }

            var found2 = SeekNextLineFeedInNegativeDirectionAndPositionStreamOnIt(stream, steps);

            if (found2)
            {
                // Ok we found a second linefeed - so one character after will be the start of our line
                SetPositionRelative(stream, 1);
            }

            // We found one LF but not another one - so there is only one line 
            // -> we can read this line if we position to the begin of the file
            else SetPosition(stream, 0);

            return true;
        }

    }


    public interface IFileStream : IDisposable
    {
        long Seek(long offset, SeekOrigin origin);
        long Position { get; set; }
        long Length { get; }
        int Read(Span<byte> buffer);
        int Read(byte[] buffer); // Mainly for unit test since [Span] is not "mock friendly"

        bool SeekLastLineFromCurrentAndPositionOnStartOfIt();

    }


    public interface IFileStreamWriter
    {
        void Write(Span<byte> buffer);
    }

    public class FileStreamWrapper : IFileStream, IFileStreamWriter
    {
        private readonly FileStream _stream;
        private bool _disposed = false;
        private readonly FileSeeker _seeker = new FileSeeker();

        public FileStreamWrapper(string path, FileMode mode, FileAccess access, FileShare share)
        {
            _stream = new FileStream(path, mode, access, share);
        }

        public FileStreamWrapper(FileStream assignStreamToThisClass)
        {
            _stream = assignStreamToThisClass;
        }

        public long Position { get => _stream.Position; set => _stream.Position = value; }

        public long Length => _stream.Length;

        public long Seek(long offset, SeekOrigin origin)
        {
            return _stream.Seek(offset, origin);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _stream?.Dispose();
            _disposed = true;
        }

        public int Read(Span<byte> buffer)
        {
            return _stream.Read(buffer);
        }

        public int Read(byte[] buffer)
        {
            return _stream.Read(buffer);
        }


        // ReSharper disable once ArrangeTypeMemberModifiers

        // ReSharper disable once UnusedMember.Local
        void SetPosition(long position)
        {
            _stream.Seek(position, SeekOrigin.Begin);
        }

        public bool SeekLastLineFromCurrentAndPositionOnStartOfIt()
        {
            return _seeker.SeekLastLineFromCurrentAndPositionOnStartOfIt(this);
        }

        public void Write(Span<byte> buffer)
        {
            _stream.Write(buffer);
        }
    }


    public class FileReadOnlyWrapper : IFile
    {
        private long _currentPosition;
        private readonly string _fileName;
        private readonly IFileStream _stream;

        private byte[]? _localBuffer; // We hold the buffer in a local variable for reuse - since we don't
                                      // want to have the GC to do that much

        private int _reallocateCounter;
        private readonly int _reallocateAfterXCountsLowerThan50Percent = 20; // If a smaller buffer would have reallocated for 20 times - then assume
                                                                             // we have allocated a really big one and free it again

        public FileReadOnlyWrapper(string fileName)
        {
            _fileName = fileName;
            (_stream, _currentPosition) = InitializeNewStream(fileName);
        }

        public void Dispose()
        {
            _stream?.Dispose();
            _currentPosition = -1;
        }



        // Will be used for unit testing only - since Moq cannot handle Span<byte>
        public string FOR_UNIT_TEST_SLOW_FUNCTION_ReadLineCharByCharTillCRLF()
        {
            byte[] buf = new byte[1];
            int index = 0;
            var pos = _stream.Position;
            for (; ; )
            {
                if (_stream.Read(buf) != 1)
                    return String.Empty;
                var c = buf[0];

                ++index;
                if (c == '\n')
                    break;
            }

            buf = new byte[index];
            _stream.Seek(pos, SeekOrigin.Begin);
            if (_stream.Read(buf) != index)
                return String.Empty;
            string complete = System.Text.Encoding.Default.GetString(buf);
            return complete;
        }

        public (string line, ReadLine sizeExceeded) ReadLineFromCurrentPositionToEnd(long maxStringSize)
        {
            var result = InternalReadLineFromCurrentPositionToEnd(maxStringSize);
            if (String.IsNullOrEmpty(result.line))
            {
                _stream?.Seek(_currentPosition, SeekOrigin.Begin);
            }
            return result;
        }
        static (FileStreamWrapper, long) InitializeNewStream(string fileName)
        {
            var stream = new FileStreamWrapper(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            var currentPosition = SeekLastLine(stream);
            return (stream, currentPosition);
        }

        static long SeekLastLine(IFileStream stream)
        {
            stream.Seek(0, SeekOrigin.End);
            var foundLine = stream.SeekLastLineFromCurrentAndPositionOnStartOfIt();
            if (!foundLine)
                return -1;
            var currentPosition = stream.Position;
            return currentPosition;
        }




        public (string line, ReadLine sizeExceeded) InternalReadLineFromCurrentPositionToEnd(long maxStringSize)
        {
            try
            {
                if (_currentPosition == -1) _currentPosition = SeekLastLine(_stream);
                ReadLine sizeExceeded = ReadLine.BufferSufficient;
                _stream.Seek(_currentPosition, SeekOrigin.Begin);

                long current = _stream.Position;
                long maxToRead = _stream.Length - current;

                if (maxToRead < 0)
                {
                    // Normally this shouldn't happen - somehow the file has a lower size than on start
                    // which would mean somebody deleted file content
                    _currentPosition = SeekLastLine(_stream);
                    return (String.Empty, sizeExceeded);
                }

                long toRead = maxToRead;
                if (toRead > maxStringSize)
                {
                    toRead = maxStringSize;
                    sizeExceeded = ReadLine.ReadLineContentExceedsSize;
                }
                if (toRead <= 0)
                    return (String.Empty, ReadLine.BufferSufficient);

                CheckIfBufferNeedsReallocation(toRead);

                Span<byte> buffer = _localBuffer.AsSpan().Slice(0, (int)toRead);
                _stream.Read(buffer);

                var lastIndex = buffer.LastIndexOf((byte)'\n');

                if (lastIndex < 0)
                {
                    _currentPosition = current;
                    return (String.Empty, ReadLine.BufferSufficient);
                }

                SetCurrentPositionAndResetBufferIfNeeded(ref buffer, ref lastIndex);

                string result = System.Text.Encoding.Default.GetString(buffer);
                return (result, sizeExceeded);
            }
            catch (Exception e)
            {
                Console.Error.Write(e.Message);
                return (String.Empty, ReadLine.BufferSufficient);
            }
        }

        private void SetCurrentPositionAndResetBufferIfNeeded(ref Span<byte> buffer, ref int lastIndex)
        {
            ++lastIndex; // Return also the \n character

            if (lastIndex < buffer.Length)
            {
                // We couldn't read a complete line at the end - so position to the last index
                _currentPosition += lastIndex;
                if (_currentPosition < 0)
                {
                    // That shouldn't happen ?!
                    _currentPosition = 0;
                }

                buffer = _localBuffer.AsSpan().Slice(0, lastIndex);
            }
            else
                _currentPosition = _stream?.Position ?? 0;
        }

        private void CheckIfBufferNeedsReallocation(long toRead)
        {
            if ((_localBuffer == null) || (_localBuffer.Length < toRead))
            {
                _localBuffer = new byte[toRead];
                _reallocateCounter = 0;
            }
            else
            {
                // Following lines shall prevent a really big buffer of memory to be held forever if not needed
                if (_localBuffer.Length > (toRead * 2))
                    ++_reallocateCounter;
                else _reallocateCounter = 0;
                if (_reallocateCounter > _reallocateAfterXCountsLowerThan50Percent)
                {
                    _reallocateCounter = 0;
                    _localBuffer = new byte[toRead];
                }
            }

        }
    }

}

