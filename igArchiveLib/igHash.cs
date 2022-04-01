using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace igArchiveLib
{
    public static class igHash
    {
        public static uint hashFileName(string name, uint basis = 0x811c9dc5)
        {
            name = name.ToLower().Replace('\\', '/');
            for (int i = 0; i < name.Length; i++)
            {
                basis = (basis ^ name[i]) * 0x1000193;
            }
            return basis;
        }
    }
}
