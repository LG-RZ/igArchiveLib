using SevenZip;
using SevenZip.Compression.LZMA;
using Ionic.Zlib;
using System.Diagnostics;

namespace igArchiveLib
{
    using FileInfo = igArchive.FileInfo;
    using CompressionType = igArchive.CompressionType;

    internal class igArchiveManager
    {
        #region Decompression

        public static async void asyncProcess(igArchive archive, FileInfo fileInfo, byte[] buffer)
        {
            int NumOfBlocks = (int)((fileInfo.Size + 0x7FFF) >> 0xF);

            Task[] decompressionTasks = new Task[NumOfBlocks];
            for (int BlockReadIndex = 0; BlockReadIndex < NumOfBlocks; BlockReadIndex++)
            {
                uint BlockOffset = 0x0;
                uint BlockSize = 0x0;
                bool IsCompressed = false;
                int BlockIndex = ((int)(fileInfo.BlockIndex & 0xfffffffU) + BlockReadIndex);

                if (0x7f * archive._archiveHeader.SectorSize < fileInfo.Size)
                {
                    if (0x7fff * archive._archiveHeader.SectorSize < fileInfo.Size)
                    {
                        uint Block = archive._largeFileBlockTable[BlockIndex];
                        BlockOffset = (Block & 0x7fffffff) * archive._archiveHeader.SectorSize;
                        IsCompressed = (byte)(Block >> 0x1f) == 1;
                        BlockSize = (archive._largeFileBlockTable[BlockIndex + 1] & 0x7fffffff) * archive._archiveHeader.SectorSize - BlockOffset;
                    }
                    else
                    {
                        uint Block = archive._mediumFileBlockTable[BlockIndex];
                        BlockOffset = (Block & 0x7fff) * archive._archiveHeader.SectorSize;
                        IsCompressed = (byte)(Block >> 0xf) == 1;
                        BlockSize = (uint)((archive._mediumFileBlockTable[BlockIndex + 1] & 0x7fff) * archive._archiveHeader.SectorSize - BlockOffset);
                    }
                }
                else
                {
                    uint Block = archive._smallFileBlockTable[BlockIndex];
                    BlockOffset = (Block & 0x7f) * archive._archiveHeader.SectorSize;
                    IsCompressed = (byte)(Block >> 0x7) == 1;
                    BlockSize = (uint)((archive._smallFileBlockTable[BlockIndex + 1] & 0x7f) * archive._archiveHeader.SectorSize - BlockOffset);
                }

                uint DecompressedSize = (fileInfo.Size < (BlockReadIndex + 1) * 0x8000) ? fileInfo.Size & 0x7fff : 0x8000;
                CompressionType Compression = IsCompressed ? (CompressionType)(fileInfo.BlockIndex >> 0x1C) : CompressionType.kUncompressed;

                byte[] BlockBuffer = new byte[BlockSize];
                archive._fileStream.Position = (fileInfo.Offset + BlockOffset);
                archive._fileStream.Read(BlockBuffer, 0, BlockBuffer.Length);
                int Temp = BlockReadIndex;
                decompressionTasks[BlockReadIndex] = Task.Run(() => decompressBlock(Temp * 0x8000, DecompressedSize, Compression, BlockBuffer, buffer));
            }

            Task t = Task.WhenAll(decompressionTasks);
            try
            {
                t.Wait();
            }
            catch { }
        }

        private unsafe static void decompressBlock(int Offset, uint DecompressedSize, CompressionType compressionFormat, byte[] source, byte[] destination)
        {
            switch (compressionFormat)
            {
                case CompressionType.kUncompressed:
                    Buffer.BlockCopy(source, 0, destination, Offset, (int)DecompressedSize);
                    break;
                case CompressionType.kZlib:
                    ushort CompressedSize = BitConverter.ToUInt16(source, 0);
                    using (var decompresser = new DeflateStream(new MemoryStream(destination, Offset, (int)DecompressedSize), CompressionMode.Decompress))
                    {
                        decompresser.Write(source, 2, CompressedSize);
                    }
                    break;
                case CompressionType.kLzma:
                    CompressedSize = BitConverter.ToUInt16(source, 0);

                    byte[] Properties = new byte[5];
                    Buffer.BlockCopy(source, 2, Properties, 0, 5);
                    Decoder decoder = new Decoder();
                    decoder.SetDecoderProperties(Properties);
                    
                    using (var srcStream = new MemoryStream(source))
                    {
                        using (var dstStream = new MemoryStream(destination))
                        {
                            srcStream.Position = 0x7;
                            dstStream.Position = Offset;
                            decoder.Code(srcStream, dstStream, CompressedSize, DecompressedSize, null);
                        }
                    }
                    break;
                case CompressionType.kLz4:
                    CompressedSize = BitConverter.ToUInt16(source, 0);
                    K4os.Compression.LZ4.LZ4Codec.Decode(source, 2, CompressedSize, destination, Offset, (int)DecompressedSize);
                    break;
            }
        }

        #endregion

        #region Compression

        #region LZMA Properties

        static CoderPropID[] propIDs =
        {
            CoderPropID.DictionarySize,
            CoderPropID.PosStateBits,
            CoderPropID.LitContextBits,
            CoderPropID.LitPosBits,
            CoderPropID.Algorithm,
            CoderPropID.NumFastBytes,
            CoderPropID.EndMarker,
            CoderPropID.MatchFinder,
        };
        static object[] properties =
        {
            0x8000,
            2,
            3,
            0,
            2,
            0x80,
            false,
            "bt4",
        };

        #endregion

        private struct CompressionBlock
        {
            public int Size;
            public byte[] Buffer;
            public byte[] Properties;
            public bool Compressed;
        }

        public static void asyncCompress(igArchive archive, FileInfo fileInfo, CompressionType compressionFormat, Stream inStream, Stream outStream)
        {
            int NumOfBlocks = (int)((fileInfo.Size + 0x7FFF) >> 0xF);
            uint BlockIndex = 0;

            int FileInfoBlockIndex = (int)(fileInfo.BlockIndex & 0xfffffffU);
            Task[] compressionTasks = new Task[NumOfBlocks];
            CompressionBlock[] compressionBlocks = new CompressionBlock[NumOfBlocks];
            for (int i = 0; i < compressionBlocks.Length; i++)
            {
                compressionBlocks[i].Buffer = new byte[0x8000];
                compressionBlocks[i].Size = inStream.Read(compressionBlocks[i].Buffer, 0, 0x8000);

                int Temp = i;

                compressionTasks[i] = Task.Run(() =>
                {
                    byte[] Properties = new byte[5];
                    byte[] PackedData = new byte[0x10000];

                    if (compressBlock(compressionBlocks[Temp].Buffer, compressionBlocks[Temp].Size, PackedData, PackedData.Length, Properties, compressionFormat, out int CompressedSize) && CompressedSize < 0x7800)
                    {
                        compressionBlocks[Temp].Size = CompressedSize;
                        compressionBlocks[Temp].Buffer = PackedData;
                        compressionBlocks[Temp].Properties = Properties;
                        compressionBlocks[Temp].Compressed = true;
                    }
                });
            }

            Task t = Task.WhenAll(compressionTasks);
            t.Wait();

            for (int Index = 0; Index < NumOfBlocks; Index++)
            {
                int BlockWriteIndex = FileInfoBlockIndex + Index;
                if (0x7f * archive._archiveHeader.SectorSize < fileInfo.Size)
                {
                    if (0x7fff * archive._archiveHeader.SectorSize < fileInfo.Size)
                    {
                        archive._largeFileBlockTable[BlockWriteIndex] = (archive._largeFileBlockTable[BlockWriteIndex] & 0x80000000 | BlockIndex);
                    }
                    else
                    {
                        archive._mediumFileBlockTable[BlockWriteIndex] = (ushort)(archive._mediumFileBlockTable[BlockWriteIndex] & 0x8000 | BlockIndex);
                    }
                }
                else
                {
                    archive._smallFileBlockTable[BlockWriteIndex] = (byte)(archive._smallFileBlockTable[BlockWriteIndex] & 0x80 | BlockIndex);
                }

                int ResultSize = compressionBlocks[Index].Size;

                if (compressionBlocks[Index].Compressed)
                {
                    outStream.Write(BitConverter.GetBytes((ushort)ResultSize));
                    if (compressionFormat == CompressionType.kLzma)
                    {
                        outStream.Write(compressionBlocks[Index].Properties);
                        outStream.Write(compressionBlocks[Index].Buffer, 0, ResultSize);
                        ResultSize += 7;
                    }
                    else
                    {
                        outStream.Write(compressionBlocks[Index].Buffer, 0, ResultSize);
                        ResultSize += 2;
                    }
                }
                else
                {
                    outStream.Write(compressionBlocks[Index].Buffer, 0, ResultSize);

                    if (0x7f * archive._archiveHeader.SectorSize < fileInfo.Size)
                    {
                        if (0x7fff * archive._archiveHeader.SectorSize < fileInfo.Size)
                        {
                            archive._largeFileBlockTable[BlockWriteIndex] = BlockIndex;
                        }
                        else
                        {
                            archive._mediumFileBlockTable[BlockWriteIndex] = (ushort)BlockIndex;
                        }
                    }
                    else
                    {
                        archive._smallFileBlockTable[BlockWriteIndex] = (byte)BlockIndex;
                    }
                }

                outStream.Position += -outStream.Position & 0x7FF;

                BlockIndex += ((uint)(ResultSize + 0x7FF) >> 0xB);
            }

            uint LastBlock = (uint)(NumOfBlocks + FileInfoBlockIndex);
            if (0x7f * archive._archiveHeader.SectorSize < fileInfo.Size)
            {
                if (0x7fff * archive._archiveHeader.SectorSize < fileInfo.Size)
                {
                    archive._largeFileBlockTable[LastBlock] = (archive._largeFileBlockTable[LastBlock] & 0x80000000 | BlockIndex);
                }
                else
                {
                    archive._mediumFileBlockTable[LastBlock] = (ushort)(archive._mediumFileBlockTable[LastBlock] & 0x8000 | BlockIndex);
                }
            }
            else
            {
                archive._smallFileBlockTable[LastBlock] = (byte)(archive._smallFileBlockTable[LastBlock] & 0x80 | BlockIndex);
            }
        }

        private static bool compressBlock(byte[] source, int sourceLength, byte[] destination, int destinationLength, byte[] Properties, CompressionType compressionFormat, out int CompressedSize)
        {
            CompressedSize = 0;
            try
            {
                switch (compressionFormat)
                {
                    case CompressionType.kZlib:
                        using (var destinationStream = new MemoryStream(destination, 0, destinationLength))
                        {
                            using (var compressor = new DeflateStream(destinationStream, CompressionMode.Compress, true) { FlushMode = FlushType.None })
                            {
                                compressor.Write(source, 0, sourceLength);
                                CompressedSize = (int)compressor.TotalOut;
                                CompressedSize = 1;
                            }
                            CompressedSize = (int)destinationStream.Position;
                        }
                        break;
                    case CompressionType.kLzma:
                        using (var inStream = new MemoryStream(source, 0, sourceLength))
                        using (var outStream = new MemoryStream(destination, 0, destinationLength))
                        {
                            Encoder encoder = new Encoder();
                            encoder.SetCoderProperties(propIDs, properties);
                            encoder.WriteCoderProperties(new MemoryStream(Properties));
                            encoder.Code(inStream, outStream, -1, -1, null);
                            CompressedSize = (int)outStream.Position;
                        }
                        break;
                    case CompressionType.kLz4:
                        CompressedSize = K4os.Compression.LZ4.LZ4Codec.Encode(source, 0, sourceLength, destination, 0, destinationLength, K4os.Compression.LZ4.LZ4Level.L03_HC);
                        break;
                }
            }
            catch
            {
                Debug.WriteLine("Encountered an issue while trying to compress block with compression format: {0}", compressionFormat);
            }
            if (CompressedSize <= 0)
                return false;
            return true;
        }

        #endregion
    }
}
