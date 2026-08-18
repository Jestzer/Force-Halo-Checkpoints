using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Force.Halo.Checkpoints.Linux.Services;

/// Everything for Halo: Campaign Evolved lives in here, on purpose. None of the
/// MCC / original-Halo code paths touch this file and this file doesn't touch them.
/// This is the Linux twin of WPF/CampaignEvolved.cs and the offset logic is deliberately
/// identical, so if one of them needs updating for a game patch, so does the other.
///
/// The game uses Unreal Engine 5, but only for rendering. The actual gameplay is still
/// the blam! engine, running inside HaloSimulation_tag_release.dll, so forcing a
/// checkpoint works the same way it always has: write 1 to a flag in the save globals.
///
/// The one real difference from the MCC games is that the sim DLL is lazily loaded.
/// It isn't in the process at the main menu, it only shows up once you're actually in
/// a mission. So the questions, "is the game running?" and "can I force a checkpoint?" are separate.
///
/// Linux-specific notes:
///
/// The game is a Windows binary running under Proton, so the PE modules are mapped into
/// an ordinary Linux process by Wine and everything below works on that process directly.
/// We don't go anywhere near ptrace for this one. The rest of the program uses
/// PTRACE_ATTACH + PEEKDATA/POKEDATA, which is why it wants root and why it retries in a
/// loop until it gets lucky. process_vm_readv/process_vm_writev need neither: they don't
/// stop the process, they don't need root as long as you own the game, and they can pull
/// the whole 2MB .rdata section over in about 4ms, which the resolver below needs.
internal static partial class CampaignEvolved
{
    // Under Proton, Wine sets the process command line to the Windows-style path of the
    // .exe, so this matches regardless of whether it was launched by Steam, Lutris,
    // Heroic or a bare wine call.
    public const string ExecutableName = "HaloCampaignEvolved.exe";

    public const string ModuleName = "HaloSimulation_tag_release.dll";

    // Offsets
    //
    // Anchor: the first instruction of the game_saving HaloScript evaluate proc, which
    // is a "cmp byte ptr [rip+disp32], 0" against the blam! save globals. Decoding that
    // one instruction gives us the save globals block without scanning anything.
    //
    // The two shipping builds of the sim DLL put that proc in slightly different places,
    // but both decode to the same save globals, so the anchor is shared:
    //
    //   0x1E7280 - Steam build (verified in game under Proton Hotfix on Fedora 44)
    //   0x1E7270 - Microsoft Store (GDK) build
    //
    // These are only a fast path. Anything that doesn't match falls through to the
    // resolver, which works the offsets out from scratch.
    private static readonly int[] knownEvaluateRvas = [0x1E7280, 0x1E7270];

    private const int KnownAnchorRva = 0x135706D;

    // The byte we actually write 1 to, expressed as a delta from the anchor rather
    // than as an absolute RVA. Both bytes live in the same save-globals struct, so
    // when a patch moves the struct they move together - the delta survives, the
    // absolute address doesn't.
    //
    // 0 means we write to the anchor itself, i.e. the flag game_saving reads. Setting
    // that is enough to kick the save system into actually producing a checkpoint.
    // (game_revert is anchor+5, if you ever want a revert button.)
    private const int CheckpointRequestDelta = 0;

    // Bytes we expect at the evaluate proc. 0x80 0x3D is "cmp byte ptr [rip+disp32], imm8".
    private static readonly byte[] anchorOpcode = [0x80, 0x3D];

    private const int AnchorInstructionLength = 7;   // 80 3D <disp32> <imm8>

    private const int PeHeaderSize = 0x1000;

    // libc bits.
    // Deliberately private and kept out of MainWindow so that nothing in here can
    // affect the existing games.

    [StructLayout(LayoutKind.Sequential)]
    private struct IoVec
    {
        public IntPtr Base;
        public IntPtr Length;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr process_vm_readv(int pid, ref IoVec localIov, ulong localIovCount,
        ref IoVec remoteIov, ulong remoteIovCount, ulong flags);

    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr process_vm_writev(int pid, ref IoVec localIov, ulong localIovCount,
        ref IoVec remoteIov, ulong remoteIovCount, ulong flags);

    private const int Eperm = 1;
    private const int Esrch = 3;

    // Offset cache
    // Keyed by something that changes when the sim DLL does, so a game update invalidates
    // it by itself. In-memory for this session, and on disk so future launches skip the
    // resolve too.

    private sealed class CachedOffsets
    {
        public string Version { get; set; } = string.Empty;
        public int EvaluateRva { get; set; }
        public int AnchorRva { get; set; }
    }

    /// Source-generated, for the same reason SettingsStore is: the Linux build is
    /// NativeAOT and reflection-based JSON silently does nothing there. Losing this cache
    /// would only cost a few milliseconds per launch, but it would do it quietly, which is
    /// worse than costing something loudly.
    [JsonSerializable(typeof(CachedOffsets))]
    private sealed partial class OffsetsJsonContext : JsonSerializerContext;

    private static CachedOffsets? sessionCache;
    private static readonly object cacheLock = new();

    private const string CacheFileName = "campaign-evolved-offsets.json";
    private const string AppFolderName = "force-halo-checkpoints";

    private static string CacheFilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName, CacheFileName);

    /// What state the game is in. The UI can use this to say something useful instead
    /// of just "it didn't work." I hate "sOmEtHiNg wEnT wRoNg" more than literally anything else.
    public enum GameState
    {
        NotRunning,
        RunningButNotInMission,
        Ready
    }

    public static GameState GetState(out string message)
    {
        if (!TryFindProcess(out _, out long moduleBase, out _))
        {
            message = "Halo: Campaign Evolved is not running.";
            return GameState.NotRunning;
        }

        if (moduleBase == 0)
        {
            message = "Halo: Campaign Evolved is running, but you aren't in a mission yet.";
            return GameState.RunningButNotInMission;
        }

        message = string.Empty;
        return GameState.Ready;
    }

    /// Actually force a checkpoint. Returns true on success, otherwise false with a reason in
    /// <paramref name="message"/> so the caller can decide whether to show an error window.
    public static bool TryForceCheckpoint(out string message)
    {
        try
        {
            if (!TryFindProcess(out int pid, out long moduleBase, out string? modulePath))
            {
                message = "Halo: Campaign Evolved is not running.";
                return false;
            }

            if (moduleBase == 0)
            {
                message = "Halo: Campaign Evolved is running, but you aren't in a mission yet.";
                return false;
            }

            if (!TryGetAnchorRva(pid, moduleBase, modulePath, out int anchorRva, out message))
            {
                return false;
            }

            byte[] buffer = [1];

            if (!TryWrite(pid, moduleBase + anchorRva + CheckpointRequestDelta, buffer, buffer.Length, out int errno))
            {
                message = "Checkpoint unsuccessfully forced (failed to write to process memory" +
                          DescribeErrno(errno) + ".)";
                return false;
            }

            message = "Checkpoint successfully forced!";
            return true;
        }
        catch (Exception ex)
        {
            message = "The attempt to force a checkpoint in Halo: Campaign Evolved failed. " +
                      "This is the automatic error message that was produced: " + ex.Message;
            return false;
        }
    }

    // The actual offset logic.

    /// Gets the save-globals anchor RVA, doing as little work as possible:
    ///
    ///   1. Already worked it out this session? Use that. (no reads)
    ///   2. On-disk cache matches this build? Verify it and use it. (one 8-byte read)
    ///   3. One of the hardcoded offsets still valid? Use it. (one 8-byte read each)
    ///   4. Otherwise the game patched, so resolve it properly. (a few ms, once)
    ///
    /// Steps 1-3 are the normal path and cost essentially nothing. Step 4 happens once
    /// per game update, not once per launch.
    private static bool TryGetAnchorRva(int pid, long moduleBase, string? modulePath, out int anchorRva, out string message)
    {
        message = string.Empty;
        anchorRva = 0;

        byte[] headers = new byte[PeHeaderSize];

        if (!TryRead(pid, moduleBase, headers, headers.Length, out int errno))
        {
            message = "Couldn't read Halo: Campaign Evolved's memory" + DescribeErrno(errno) + ". " +
                      HelpForErrno(errno);
            return false;
        }

        if (!TryParsePeHeaders(headers, out int sizeOfImage, out Dictionary<string, (int Rva, int Size)> sections))
        {
            message = $"Found {ModuleName} in the game, but it doesn't look like a PE image. " +
                      "Please report this so the program can be updated.";
            return false;
        }

        string version = DescribeBuild(modulePath, sizeOfImage);

        lock (cacheLock)
        {
            // 1. This session.
            if (sessionCache != null && sessionCache.Version == version)
            {
                anchorRva = sessionCache.AnchorRva;
                return true;
            }

            // 2. Previous session.
            CachedOffsets? onDisk = LoadCache();

            if (onDisk != null && onDisk.Version == version &&
                VerifyAnchor(pid, moduleBase, onDisk.EvaluateRva, onDisk.AnchorRva))
            {
                sessionCache = onDisk;
                anchorRva = onDisk.AnchorRva;
                return true;
            }

            // 3. The hardcoded offsets from the builds this was written against.
            foreach (int knownEvaluateRva in knownEvaluateRvas)
            {
                if (!VerifyAnchor(pid, moduleBase, knownEvaluateRva, KnownAnchorRva))
                {
                    continue;
                }

                Remember(version, knownEvaluateRva, KnownAnchorRva);
                anchorRva = KnownAnchorRva;
                return true;
            }

            // 4. The game was patched and the offsets moved. Work them out again.
            if (TryResolveAnchor(pid, moduleBase, sizeOfImage, sections, out int resolvedEvaluateRva, out int resolvedAnchorRva))
            {
                Remember(version, resolvedEvaluateRva, resolvedAnchorRva);
                anchorRva = resolvedAnchorRva;
                return true;
            }

            message = "Halo: Campaign Evolved has been updated and the checkpoint offsets no longer " +
                      "match, and they couldn't be worked out automatically. Please report this so " +
                      "the program can be updated.";
            return false;
        }
    }

    private static void Remember(string version, int evaluateRva, int anchorRva)
    {
        sessionCache = new CachedOffsets
        {
            Version = version,
            EvaluateRva = evaluateRva,
            AnchorRva = anchorRva
        };
        SaveCache(sessionCache);
    }

    /// TL;DR
    /// A sanity check. Reads the 8 bytes at the evaluate proc and confirms it's still
    /// the "cmp byte ptr [rip+disp32], 0" we'd expect, pointing at the flag we'd expect.
    ///
    /// This is the bit that matters even if you never use the resolver: without it, a
    /// patch that moves things means writing 1 to an arbitrary byte of the game's memory.
    /// With it, that turns into a clean error message instead.
    /// TL;DR
    private static bool VerifyAnchor(int pid, long moduleBase, int evaluateRva, int expectedAnchorRva)
    {
        return TryDecodeAnchor(pid, moduleBase, evaluateRva, out int decodedAnchorRva) &&
               decodedAnchorRva == expectedAnchorRva;
    }

    /// Reads the instruction at <paramref name="evaluateRva"/> and, if it's the compare we
    /// expect, follows its RIP-relative displacement to the byte it reads.
    private static bool TryDecodeAnchor(int pid, long moduleBase, int evaluateRva, out int anchorRva)
    {
        anchorRva = 0;

        byte[] code = new byte[8];

        if (!TryRead(pid, moduleBase + evaluateRva, code, code.Length, out _))
        {
            return false;
        }

        if (code[0] != anchorOpcode[0] || code[1] != anchorOpcode[1])
        {
            return false;
        }

        anchorRva = evaluateRva + AnchorInstructionLength + BitConverter.ToInt32(code, 2);
        return true;
    }

    /// Works out the offsets from scratch. This is not a signature scan of the whole
    /// executable - it's three targeted lookups inside .rdata:
    ///
    ///   1. Find the string "game_saving"
    ///   2. Find the pointer to it (the name field of Blam's hs_function_definition)
    ///   3. read the evaluate proc from that struct and decode its first instruction
    ///
    /// Everything is read out of the live process rather than off disk, which avoids
    /// having to work out where Steam put the game and which Wine prefix it's in.
    private static bool TryResolveAnchor(int pid, long moduleBase, int sizeOfImage,
        Dictionary<string, (int Rva, int Size)> sections, out int evaluateRva, out int anchorRva)
    {
        evaluateRva = 0;
        anchorRva = 0;

        // The size check isn't paranoia for its own sake: it's read straight out of the
        // headers and it's about to become the size of an allocation.
        if (!sections.TryGetValue(".rdata", out (int Rva, int Size) rdataSection) ||
            rdataSection.Rva <= 0 || rdataSection.Size <= 0 ||
            rdataSection.Rva + (long)rdataSection.Size > sizeOfImage)
        {
            return false;
        }

        byte[] rdata = new byte[rdataSection.Size];

        if (!TryRead(pid, moduleBase + rdataSection.Rva, rdata, rdata.Length, out _))
        {
            return false;
        }

        // 1. The string, with the NULs either side so we don't match a longer name.
        int stringIndex = IndexOf(rdata, Encoding.ASCII.GetBytes("\0game_saving\0"), 0, 1);

        if (stringIndex < 0)
        {
            return false;
        }

        long stringAddress = moduleBase + rdataSection.Rva + stringIndex + 1;

        // 2. The pointer to it. These are already relocated in memory (and Wine relocates
        //    them too, since it never gets the preferred base), so we compare against the
        //    real runtime address rather than anything out of the PE.
        //
        //    Search 8 bytes at a time: a pointer is always 8-aligned, and both the module
        //    base and the section RVA are, so index alignment matches address alignment.
        //    Every match gets tried in turn in case something else in .rdata also points
        //    at the string - the first one that decodes to a real compare instruction wins.
        byte[] pointerBytes = BitConverter.GetBytes(stringAddress);
        int pointerIndex = IndexOf(rdata, pointerBytes, 0, 8);

        while (pointerIndex >= 0)
        {
            if (pointerIndex + 32 > rdata.Length)
            {
                return false;
            }

            // 3. hs_function_definition: +16 is the parse proc (shared by every script
            //    function), +24 is the evaluate proc, which is the one we want.
            long evaluateAddress = BitConverter.ToInt64(rdata, pointerIndex + 24);
            long candidateRva = evaluateAddress - moduleBase;

            if (candidateRva > 0 && candidateRva < sizeOfImage &&
                TryDecodeAnchor(pid, moduleBase, (int)candidateRva, out int candidateAnchorRva) &&
                candidateAnchorRva > 0 && candidateAnchorRva < sizeOfImage)
            {
                evaluateRva = (int)candidateRva;
                anchorRva = candidateAnchorRva;
                return true;
            }

            pointerIndex = IndexOf(rdata, pointerBytes, pointerIndex + 8, 8);
        }

        return false;
    }

    /// Reads the PE headers out of the mapped image: SizeOfImage plus every section's
    /// RVA and virtual size.
    private static bool TryParsePeHeaders(byte[] headers, out int sizeOfImage,
        out Dictionary<string, (int Rva, int Size)> sections)
    {
        sizeOfImage = 0;
        sections = new Dictionary<string, (int, int)>(StringComparer.Ordinal);

        if (headers[0] != (byte)'M' || headers[1] != (byte)'Z')
        {
            return false;
        }

        int peOffset = BitConverter.ToInt32(headers, 0x3C);

        if (peOffset <= 0 || peOffset + 0x108 > headers.Length)
        {
            return false;
        }

        if (BitConverter.ToUInt32(headers, peOffset) != 0x00004550) // "PE\0\0"
        {
            return false;
        }

        int coff = peOffset + 4;
        int sectionCount = BitConverter.ToUInt16(headers, coff + 2);
        int optionalHeaderSize = BitConverter.ToUInt16(headers, coff + 16);
        int optionalHeader = coff + 20;
        int sectionTable = optionalHeader + optionalHeaderSize;

        sizeOfImage = BitConverter.ToInt32(headers, optionalHeader + 56);

        if (sizeOfImage <= 0)
        {
            return false;
        }

        for (int i = 0; i < sectionCount; i++)
        {
            int header = sectionTable + (i * 40);

            if (header + 40 > headers.Length)
            {
                return false;
            }

            string name = Encoding.ASCII.GetString(headers, header, 8).TrimEnd('\0');
            int virtualSize = BitConverter.ToInt32(headers, header + 8);
            int virtualAddress = BitConverter.ToInt32(headers, header + 12);
            sections[name] = (virtualAddress, virtualSize);
        }

        return sections.Count > 0;
    }

    // Finding the game.

    /// Finds the game and, in the same breath, where Wine has put the simulation.
    ///
    /// Proton launches the game behind a stack of wrappers (reaper, pressure-vessel, the
    /// proton script itself, steam.exe) and every one of them carries the .exe path in its
    /// command line, so matching on that alone picks the wrong process. Only the real Wine
    /// process has the PE mapped into it, so that's the tiebreaker.
    ///
    /// <paramref name="moduleBase"/> comes back as 0 when the game is running but the
    /// simulation hasn't been loaded yet, i.e. you aren't in a mission.
    private static bool TryFindProcess(out int pid, out long moduleBase, out string? modulePath)
    {
        pid = -1;
        moduleBase = 0;
        modulePath = null;

        foreach (string entry in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(entry), out int candidate))
            {
                continue;
            }

            try
            {
                string cmdline = File.ReadAllText($"/proc/{candidate}/cmdline");

                if (cmdline.IndexOf(ExecutableName, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }
            }
            catch (Exception)
            {
                // Processes come and go while we're looking at them, and we can't read
                // the ones we don't own. Neither is the game, so move on.
                continue;
            }

            if (TryScanMaps(candidate, out moduleBase, out modulePath))
            {
                pid = candidate;
                return true;
            }
        }

        return false;
    }

    /// One pass over a process's memory map, answering both questions we have about it:
    /// is this actually the game, and if so, where is the simulation mapped?
    ///
    /// It's one pass because the game's map is 15,000-odd lines and reading it is by far
    /// the most expensive thing this file does - more than the offset resolver.
    ///
    /// Wine maps the headers of an image from the file and then fills the rest in as
    /// anonymous memory, so a module usually only shows up on a couple of lines and the
    /// image base is the lowest of them. There's no ProcessModule to ask on this side of
    /// the fence, hence doing it the long way.
    private static bool TryScanMaps(int pid, out long moduleBase, out string? modulePath)
    {
        moduleBase = 0;
        modulePath = null;
        bool isTheGame = false;

        try
        {
            foreach (string line in File.ReadLines($"/proc/{pid}/maps"))
            {
                // start-end perms offset dev inode path
                // The path can contain spaces ("Program Files (x86)", "Halo Campaign
                // Evolved"), and nothing before it does, so slice at the first slash
                // rather than splitting on whitespace.
                int pathStart = line.IndexOf('/');

                if (pathStart < 0)
                {
                    continue;
                }

                string path = line[pathStart..].TrimEnd();

                if (path.EndsWith(" (deleted)", StringComparison.Ordinal))
                {
                    path = path[..^10];
                }

                string fileName = Path.GetFileName(path);

                if (string.Equals(fileName, ExecutableName, StringComparison.OrdinalIgnoreCase))
                {
                    isTheGame = true;
                    continue;
                }

                if (!string.Equals(fileName, ModuleName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int dash = line.IndexOf('-');

                if (dash <= 0 || !ulong.TryParse(line[..dash], NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                        out ulong start))
                {
                    continue;
                }

                if (moduleBase == 0 || (long)start < moduleBase)
                {
                    moduleBase = (long)start;
                    modulePath = path;
                }
            }
        }
        catch (Exception)
        {
            // The process exited from under us, or it isn't ours to look at.
            return false;
        }

        return isTheGame;
    }

    /// Something that changes when the sim DLL does, so the cache invalidates itself on a
    /// game update. Wine doesn't hand us a file version, so this pairs the DLL's size on
    /// disk with the image's size in memory. It's only an optimisation - anything that
    /// comes back out of the cache is verified before it's used.
    private static string DescribeBuild(string? modulePath, int sizeOfImage)
    {
        long fileLength = -1;

        try
        {
            if (modulePath != null)
            {
                fileLength = new FileInfo(modulePath).Length;
            }
        }
        catch (Exception)
        {
            // The game lives somewhere we can't stat. The image size alone will do.
        }

        return $"{fileLength}-{sizeOfImage:X}";
    }

    // Reading and writing the game's memory.

    private static unsafe bool TryRead(int pid, long address, byte[] buffer, int length, out int errno)
    {
        errno = 0;

        fixed (byte* local = buffer)
        {
            IoVec localIov = new() { Base = (IntPtr)local, Length = (IntPtr)length };
            IoVec remoteIov = new() { Base = (IntPtr)address, Length = (IntPtr)length };

            if (process_vm_readv(pid, ref localIov, 1, ref remoteIov, 1, 0) == (IntPtr)length)
            {
                return true;
            }

            errno = Marshal.GetLastWin32Error();
        }

        // Hardened kernels can have process_vm_readv locked down while /proc/<pid>/mem
        // still works, so it's worth the second try before giving up.
        return TryReadViaProcMem(pid, address, buffer, length);
    }

    private static unsafe bool TryWrite(int pid, long address, byte[] buffer, int length, out int errno)
    {
        errno = 0;

        fixed (byte* local = buffer)
        {
            IoVec localIov = new() { Base = (IntPtr)local, Length = (IntPtr)length };
            IoVec remoteIov = new() { Base = (IntPtr)address, Length = (IntPtr)length };

            if (process_vm_writev(pid, ref localIov, 1, ref remoteIov, 1, 0) == (IntPtr)length)
            {
                return true;
            }

            errno = Marshal.GetLastWin32Error();
        }

        return TryWriteViaProcMem(pid, address, buffer, length);
    }

    private static bool TryReadViaProcMem(int pid, long address, byte[] buffer, int length)
    {
        try
        {
            using FileStream memory = new($"/proc/{pid}/mem", FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1);
            memory.Seek(address, SeekOrigin.Begin);

            int total = 0;

            while (total < length)
            {
                int read = memory.Read(buffer, total, length - total);

                if (read <= 0)
                {
                    return false;
                }

                total += read;
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryWriteViaProcMem(int pid, long address, byte[] buffer, int length)
    {
        try
        {
            using FileStream memory = new($"/proc/{pid}/mem", FileMode.Open, FileAccess.Write, FileShare.ReadWrite, 1);
            memory.Seek(address, SeekOrigin.Begin);
            memory.Write(buffer, 0, length);
            memory.Flush();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string DescribeErrno(int errno)
    {
        return errno == 0 ? string.Empty : $": {Marshal.GetPInvokeErrorMessage(errno)}";
    }

    private static string HelpForErrno(int errno)
    {
        return errno switch
        {
            Eperm => "The kernel wouldn't let this program look at the game. Check that " +
                     "/proc/sys/kernel/yama/ptrace_scope is 0 (it is by default on Fedora, but not on " +
                     "every distro), or run this program with sudo.",
            Esrch => "The game closed while this program was looking at it. Try again.",
            _ => "Please submit a bug report if this keeps happening."
        };
    }

    // Other shit.

    private static int IndexOf(byte[] haystack, byte[] needle, int start, int step)
    {
        int last = haystack.Length - needle.Length;

        for (int i = start; i <= last; i += step)
        {
            int j = 0;

            while (j < needle.Length && haystack[i + j] == needle[j])
            {
                j++;
            }

            if (j == needle.Length)
            {
                return i;
            }
        }

        return -1;
    }

    private static CachedOffsets? LoadCache()
    {
        try
        {
            if (!File.Exists(CacheFilePath))
            {
                return null;
            }

            return JsonSerializer.Deserialize(File.ReadAllText(CacheFilePath), OffsetsJsonContext.Default.CachedOffsets);
        }
        catch (Exception)
        {
            // A bad or unreadable cache file just means we work the offsets out again.
            return null;
        }
    }

    private static void SaveCache(CachedOffsets offsets)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CacheFilePath)!);
            File.WriteAllText(CacheFilePath, JsonSerializer.Serialize(offsets, OffsetsJsonContext.Default.CachedOffsets));
        }
        catch (Exception)
        {
            // Somewhere unwritable, or a read-only home. Don't care. We just work the
            // offsets out again next launch, which is a few milliseconds.
        }
    }
}
