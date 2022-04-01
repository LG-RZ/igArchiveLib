using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace igArchiveLib.Extensions.IO
{
    public class ExtendedBinaryWriter : BinaryWriter
    {
        #region Constructors

        public ExtendedBinaryWriter(Stream input) : base(input)
        {
        }

        public ExtendedBinaryWriter(Stream input, Encoding encoding) : base(input, encoding)
        {
        }

        public ExtendedBinaryWriter(Stream input, Encoding encoding, bool leaveOpen) : base(input, encoding, leaveOpen)
        {
        }

        #endregion

        #region Methods

        public void WriteNullTerminatedString(string value)
        {
            Write(Encoding.UTF8.GetBytes(value));
            Write(new byte());
        }

        public void WriteStruct<T>(T value)
            where T : struct
        {
            Write(MemoryUtils.StructToBytes(value));
        }

        public void WriteArray<T>(T[] value)
            where T : struct
        {
            if(typeof(T).IsPrimitive)
            {
                byte[] buffer = new byte[Marshal.SizeOf<T>() * value.Length];
                Buffer.BlockCopy(value, 0, buffer, 0, buffer.Length);
                Write(buffer);
            }
            else
            {
                for(int i = 0; i < value.Length; i++)
                {
                    Write(MemoryUtils.StructToBytes(value[i]));
                }
            }
        }

        #endregion
    }
}
