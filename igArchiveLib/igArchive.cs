using igArchiveLib.Extensions.IO;
using System.Runtime.InteropServices;

namespace igArchiveLib
{
    public class igArchive
    {
        #region Structures

        [StructLayout(LayoutKind.Sequential, Pack = 1, Size = 0x38)]
        public struct Header
        {
            public uint MagicNumber = 0x1A414749;
            public uint Version = 0xB;
            public uint TocSize;
            public uint NumFiles;
            public uint SectorSize = 0x800;
            public uint HashSearchDivider;
            public uint HashSearchSlop;
            public uint NumLargeFileBlocks;
            public uint NumMediumFileBlocks;
            public uint NumSmallFileBlocks;
            public ulong NameTableOffset;
            public uint NameTableSize;
            public uint Flags = 0x1;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1, Size = 0x10)]
        public struct FileInfo
        {
            public uint Offset;
            public int Ordinal;
            public uint Size;
            public int  BlockIndex;
        }

        public enum CompressionType : byte
        {
            kUncompressed = 0x0,
            kZlib = 0x1,
            kLzma = 0x2,
            kLz4 = 0x3
        }

        #endregion

        #region MetaFields

        public Stream _fileStream;
        internal Header _archiveHeader;

        #region Table Of Contents

        public uint[] _fileIdTable;
        public FileInfo[] _fileInfoTable;

        public uint[] _largeFileBlockTable;
        public ushort[] _mediumFileBlockTable;
        public byte[] _smallFileBlockTable;

        #endregion

        #region Name Table

        public string[] _nameTable;
        public string[] _logicalNameTable;

        #endregion

        #endregion

        #region Reading

        public void open(string path) => open(File.OpenRead(path));

        public void open(Stream stream)
        {
            _fileStream = stream;

            using(ExtendedBinaryReader reader = new ExtendedBinaryReader(stream, System.Text.Encoding.Default, true))
            {
                _archiveHeader = reader.ReadStruct<Header>();

                loadArchiveTableOfContents(reader);
                loadArchiveNameTable(reader);

                reader.BaseStream.Position = 0;
            }
        }

        private void loadArchiveTableOfContents(ExtendedBinaryReader reader)
        {
            _fileIdTable = reader.ReadArrayUnsafe<uint>((int)_archiveHeader.NumFiles);
            _fileInfoTable = reader.ReadArrayUnsafe<FileInfo>((int)_archiveHeader.NumFiles);
            _largeFileBlockTable = reader.ReadArrayUnsafe<uint>((int)_archiveHeader.NumLargeFileBlocks);
            _mediumFileBlockTable = reader.ReadArrayUnsafe<ushort>((int)_archiveHeader.NumMediumFileBlocks);
            _smallFileBlockTable = reader.ReadArrayUnsafe<byte>((int)_archiveHeader.NumSmallFileBlocks);
        }

        private unsafe void loadArchiveNameTable(ExtendedBinaryReader reader)
        {
            reader.BaseStream.Position = (long)_archiveHeader.NameTableOffset;

            fixed (byte* NameTablePointer = reader.ReadBytes((int)_archiveHeader.NameTableSize))
            {
                _nameTable = new string[_archiveHeader.NumFiles];
                _logicalNameTable = new string[_archiveHeader.NumFiles];

                for (int i = 0; i < _archiveHeader.NumFiles; i++)
                {
                    sbyte* namePointer = (sbyte*)(NameTablePointer + *(int*)(NameTablePointer + (i * 4)));
                    _nameTable[i] = new string(namePointer);
                    _logicalNameTable[i] = new string(namePointer + _nameTable[i].Length + 1);
                }
            }
        }

        public byte[] read(FileInfo fileInfo)
        {
            byte[] buffer = new byte[fileInfo.Size];
            if (fileInfo.BlockIndex != -1)
            {
                igArchiveManager.asyncProcess(this, fileInfo, buffer);
            }
            else
            {
                _fileStream.Position = fileInfo.Offset;
                _fileStream.Read(buffer, 0, buffer.Length);
            }
            return buffer;
        }

        #endregion

        #region Write

        public void createFromDirectory(string path, string directory, CompressionType compression, Action<string>? ProgressReport = null, Action<float>? PercentageReport = null)
        {
            if (_fileStream != null)
                close();
            _fileStream = File.Create(path);

            System.IO.FileInfo[] files = Directory.GetFiles(directory, "", SearchOption.AllDirectories).Select(x => new System.IO.FileInfo(x)).ToArray();

            _archiveHeader = new Header();
            _archiveHeader.NumFiles = (uint)files.Length;

            calculateFileInfoTable(files, compression);
            calculateFileIdTable(directory, files);
            Array.Sort(_fileIdTable.ToArray(), _fileInfoTable);
            Array.Sort(_fileIdTable, files);
            calculateNameTables(directory, files);
            calculateHashSearchProperties();
            if (compression != CompressionType.kUncompressed)
            {
                calculateBlockTables();
            }
            else
            {
                _largeFileBlockTable = new uint[0];
                _mediumFileBlockTable = new ushort[0];
                _smallFileBlockTable = new byte[0];
            }
            _archiveHeader.NumLargeFileBlocks = (uint)_largeFileBlockTable.Length;
            _archiveHeader.NumMediumFileBlocks = (uint)_mediumFileBlockTable.Length;
            _archiveHeader.NumSmallFileBlocks = (uint)_smallFileBlockTable.Length;
            _archiveHeader.TocSize = (_archiveHeader.NumFiles * 0x14) + (_archiveHeader.NumLargeFileBlocks * 0x4) + (_archiveHeader.NumMediumFileBlocks * 0x2) + _archiveHeader.NumSmallFileBlocks;
            using(ExtendedBinaryWriter writer = new ExtendedBinaryWriter(_fileStream, System.Text.Encoding.Default, true))
            {
                writer.WriteStruct(_archiveHeader);
                writer.WriteArray(_fileIdTable);

                long FileInfoTablePosition = writer.BaseStream.Position;
                writer.Write(new byte[_archiveHeader.NumFiles * 0x10]);

                long BlockTablesPosition = writer.BaseStream.Position;
                writer.WriteArray(_largeFileBlockTable);
                writer.WriteArray(_mediumFileBlockTable);
                writer.Write(_smallFileBlockTable);

                AlignStream(writer.BaseStream);

                for(int i = 0; i < _archiveHeader.NumFiles; i++)
                {
                    var file = _fileInfoTable[i];
                    _fileInfoTable[i].Offset = (uint)writer.BaseStream.Position;

                    if (compression == CompressionType.kUncompressed)
                    {
                        using (var fileStream = File.OpenRead(files[i].FullName))
                            fileStream.CopyTo(writer.BaseStream);

                        AlignStream(writer.BaseStream);
                    }
                    else
                    {
                        using(var fileStream = File.OpenRead(files[i].FullName))
                            igArchiveManager.asyncCompress(this, file, compression, fileStream, writer.BaseStream);
                    }

                    PercentageReport?.Invoke((i + 1) / (float)_archiveHeader.NumFiles);
                    ProgressReport?.Invoke(string.Format("Sucessfully added {0} to archive!", _logicalNameTable[i]));
                }

                writer.BaseStream.Position = FileInfoTablePosition;

                writer.WriteArray(_fileInfoTable);

                writer.BaseStream.Position = BlockTablesPosition;

                writer.WriteArray(_largeFileBlockTable);
                writer.WriteArray(_mediumFileBlockTable);
                writer.WriteArray(_smallFileBlockTable);

                writer.BaseStream.Seek(0, SeekOrigin.End);

                AlignStream(writer.BaseStream);

                var NameTableOffset = writer.BaseStream.Position;
                var NameOffsetPosition = (4 * files.Length);

                using (ExtendedBinaryWriter nameTableWriter = new ExtendedBinaryWriter(new MemoryStream()))
                {
                    for (int i = 0; i < files.Length; i++)
                    {
                        long NamePositionStart = nameTableWriter.BaseStream.Position;
                        nameTableWriter.WriteNullTerminatedString(_nameTable[i]);
                        nameTableWriter.WriteNullTerminatedString(_logicalNameTable[i]);
                        nameTableWriter.Write(new byte[4]);

                        writer.Write(NameOffsetPosition);

                        NameOffsetPosition += (int)(nameTableWriter.BaseStream.Position - NamePositionStart);
                    }

                    nameTableWriter.BaseStream.Position = 0;
                    nameTableWriter.BaseStream.CopyTo(writer.BaseStream);
                }

                _archiveHeader.NameTableOffset = (ulong)NameTableOffset;
                _archiveHeader.NameTableSize = (uint)NameOffsetPosition;

                writer.BaseStream.Position = 0;

                writer.WriteStruct(_archiveHeader);
            }
        }

        private void calculateFileInfoTable(System.IO.FileInfo[] files, CompressionType compression)
        {
            _fileInfoTable = new FileInfo[files.Length];
            for (int i = 0; i < files.Length; i++)
            {
                _fileInfoTable[i].Ordinal = ((i - 1) << 0x8);
                _fileInfoTable[i].Size = (uint)files[i].Length;
                if (compression == CompressionType.kUncompressed)
                    _fileInfoTable[i].BlockIndex = -1;
                else
                    _fileInfoTable[i].BlockIndex = ((int)compression << 0x1c);
            }
        }

        private void calculateNameTables(string rootPath, System.IO.FileInfo[] files)
        {
            _nameTable = new string[files.Length];
            _logicalNameTable = new string[files.Length];

            for(int i = 0; i < files.Length; i++)
            {
                _logicalNameTable[i] = files[i].FullName.Substring(rootPath.Length + 1).Replace('\\', '/');
                _nameTable[i] = "temporary/mack/data/win64/output/" + _logicalNameTable[i];
            }
        }

        private void calculateFileIdTable(string rootPath, System.IO.FileInfo[] files)
        {
            _fileIdTable = new uint[files.Length];

            for (int i = 0; i < files.Length; i++)
            {
                _fileIdTable[i] = igHash.hashFileName(files[i].FullName.Substring(rootPath.Length + 1).Replace('\\', '/'));
            }
        }

        private void calculateHashSearchProperties()
        {
            _archiveHeader.HashSearchDivider = uint.MaxValue / _archiveHeader.NumFiles;

            int TopMatchIndex = 0;
            for (int i = 0x0; i < _fileIdTable.Length; i++)
            {
                int Matches = 0;

                for (int j = 0x0; j < _fileIdTable.Length; j++)
                {
                    if (hashSearch(_fileIdTable, (uint)_fileIdTable.Length, _archiveHeader.HashSearchDivider, (uint)i, _fileIdTable[j]) != -1)
                        Matches++;
                }

                if (Matches == _fileIdTable.Length)
                {
                    TopMatchIndex = i;
                    break;
                }
            }

            _archiveHeader.HashSearchSlop = (uint)TopMatchIndex;
        }

        private void calculateBlockTables()
        {
            var largeFileBlockTableList = new List<uint>();
            var mediumFileBlockTableList = new List<ushort>();
            var smallFileBlockTableList = new List<byte>();

            for (int j = 0; j < _fileInfoTable.Length; j++)
            {
                var BlockCount = (_fileInfoTable[j].Size + 0x7FFF) >> 0xF;

                if (0x7f * _archiveHeader.SectorSize < _fileInfoTable[j].Size)
                {
                    if (0x7fff * _archiveHeader.SectorSize < _fileInfoTable[j].Size)
                    {
                        _fileInfoTable[j].BlockIndex |= largeFileBlockTableList.Count;

                        for (int i = 0; i < BlockCount; i++)
                            largeFileBlockTableList.Add(0x80000000);

                        largeFileBlockTableList.Add(0x0);
                    }
                    else
                    {
                        _fileInfoTable[j].BlockIndex |= mediumFileBlockTableList.Count;

                        for (int i = 0; i < BlockCount; i++)
                            mediumFileBlockTableList.Add(0x8000);

                        mediumFileBlockTableList.Add(0x0);
                    }
                }
                else
                {
                    _fileInfoTable[j].BlockIndex |= smallFileBlockTableList.Count;

                    for (int i = 0; i < BlockCount; i++)
                        smallFileBlockTableList.Add(0x80);

                    smallFileBlockTableList.Add(0x0);
                }
            }

            _largeFileBlockTable = largeFileBlockTableList.ToArray();
            _mediumFileBlockTable = mediumFileBlockTableList.ToArray();
            _smallFileBlockTable = smallFileBlockTableList.ToArray();
        }

        #endregion

        #region Static Methods

        private static int hashSearch(uint[] fileIdTable, uint numFiles, uint hashSearchDivider, uint hashSearchSlop, uint fileId)
        {
            uint fileIdDivided = fileId / hashSearchDivider;
            uint searchAt = 0;
            if (hashSearchSlop < fileIdDivided)
                searchAt = (fileIdDivided - hashSearchSlop);

            fileIdDivided += hashSearchSlop + 1;
            if (fileIdDivided < numFiles)
                numFiles = fileIdDivided;

            uint index = searchAt;
            searchAt = (numFiles - index);
            uint i = searchAt;
            while (0 < i)
            {
                i = searchAt / 2;
                if (fileIdTable[index + i] < fileId)
                {
                    index += i + 1;
                    i = searchAt - 1 - i;
                }
                searchAt = i;
            }

            if (index < fileIdTable.Length && fileIdTable[index] == fileId)
            {
                return (int)index;
            }

            return -1;
        }

        private static void AlignStream(Stream stream)
        {
            stream.Position += -stream.Position & 0x7FF;
        }

        #endregion

        public void close()
        {
            _fileStream?.Dispose();
            _fileIdTable = null;
            _fileInfoTable = null;
            _largeFileBlockTable = null;
            _mediumFileBlockTable = null;
            _smallFileBlockTable = null;
            _nameTable = null;
            _logicalNameTable = null;
        }
    }
}