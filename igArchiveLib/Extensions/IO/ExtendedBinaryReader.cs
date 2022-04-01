﻿using System.Runtime.InteropServices;
using System.Text;

namespace igArchiveLib.Extensions.IO
{
    public class ExtendedBinaryReader : BinaryReader
    {
        #region Constructors

        public ExtendedBinaryReader(Stream input) : base(input)
        {
        }

        public ExtendedBinaryReader(Stream input, Encoding encoding) : base(input, encoding)
        {
        }

        public ExtendedBinaryReader(Stream input, Encoding encoding, bool leaveOpen) : base(input, encoding, leaveOpen)
        {
        }

        #endregion

        #region Methods

        #region Safe

        public T ReadStruct<T>() where T : struct
        {
            return MemoryUtils.ToStruct<T>(ReadBytes(Marshal.SizeOf<T>()));
        }

        public T[] ReadArray<T>(int count) where T : struct
        {
            T[] data = new T[count];

            if (typeof(T).IsPrimitive)
            {
                Buffer.BlockCopy(ReadBytes(count * Marshal.SizeOf<T>()), 0, data, 0, count * Marshal.SizeOf<T>());
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    data[i] = ReadStruct<T>();
                }
            }

            return data;
        }

        public string ReadNullTerminatedString()
        {
            StringBuilder builder = new StringBuilder();
            char c;
            while ((c = ReadChar()) != '\0')
                builder.Append(c);
            return builder.ToString();
        }

        #endregion

        #region Unsafe

        public unsafe T[] ReadArrayUnsafe<T>(int count) where T : unmanaged
        {
            var buffer = ReadBytes(count * sizeof(T));
            var result = new T[count];

            fixed (byte* a = buffer)
            fixed (T* b = result)
                Buffer.MemoryCopy(a, b, buffer.Length, buffer.Length);

            return result;
        }

        #endregion

        #endregion
    }
}
