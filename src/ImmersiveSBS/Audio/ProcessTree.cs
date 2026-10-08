using System.Runtime.InteropServices;

namespace ImmersiveSBS.Audio;

/// <summary>
/// A process and all its descendants. Browsers play sound from a child process (Chrome's and
/// Edge's audio service), not from the process that owns the window.
/// </summary>
public static class ProcessTree
{
    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>The ids of <paramref name="root"/> and every process descended from it.</summary>
    public static HashSet<uint> Of(uint root)
    {
        var children = new Dictionary<uint, List<uint>>();
        IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == InvalidHandle) return [root];
        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            for (bool ok = Process32FirstW(snapshot, ref entry); ok; ok = Process32NextW(snapshot, ref entry))
            {
                // A process whose parent id was reused can look like its own ancestor; skip self-links.
                if (entry.th32ParentProcessID == entry.th32ProcessID) continue;
                if (!children.TryGetValue(entry.th32ParentProcessID, out var list))
                    children[entry.th32ParentProcessID] = list = [];
                list.Add(entry.th32ProcessID);
            }
        }
        finally
        {
            CloseHandle(snapshot);
        }

        var tree = new HashSet<uint> { root };
        var queue = new Queue<uint>([root]);
        while (queue.Count > 0)
        {
            if (!children.TryGetValue(queue.Dequeue(), out var list)) continue;
            foreach (uint child in list)
                if (tree.Add(child)) queue.Enqueue(child);
        }
        return tree;
    }
}
