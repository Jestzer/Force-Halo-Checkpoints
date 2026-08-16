using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Force.Halo.Checkpoints
{
    /// Everything for Halo: Campaign Evolved lives in here, on purpose. None of the
    /// MCC / original-Halo code paths touch this file and this file doesn't touch them.
    ///
    /// The game uses Unreal Engine 5, but only for rendering. The actual gameplay is still
    /// the blam! engine, running inside HaloSimulation_tag_release.dll, so forcing a
    /// checkpoint works the same way it always has: write 1 to a flag in the save globals.
    ///
    /// The one real difference from the MCC games is that the sim DLL is lazily loaded.
    /// It isn't in the process at the main menu, it only shows up once you're actually in
    /// a mission. So the questions, "is the game running?" and "can I force a checkpoint?" are separate.
    public static class CampaignEvolved
    {
        // Xbox/Microsoft Store (GDK) build. If the Steam build ever uses a different
        // process name, add it here - the module name is the same either way. I wouldn't know because
        // I refunded it because it ran like absolute shit originally.
        private static readonly string[] processNames = ["HaloCampaignEvolved"];

        public const string ModuleName = "HaloSimulation_tag_release.dll";

        // Offsets
        //
        // Anchor: the first instruction of the game_saving HaloScript evaluate proc, which
        // is a "cmp byte ptr [rip+disp32], 0" against the blam! save globals. Decoding that
        // one instruction gives us the save globals block without scanning anything.
        //
        // Known-good for HaloSimulation_tag_release.dll 1.111.2544.0:
        private const int KnownEvaluateRva = 0x1E7270;   // game_saving evaluate proc
        private const int KnownAnchorRva = 0x135706D;    // the byte that proc reads

        // The byte we actually write 1 to, expressed as a delta from the anchor rather
        // than as an absolute RVA. Both bytes live in the same save-globals struct, so
        // when a patch moves the struct they move together - the delta survives, the
        // absolute address doesn't.
        //
        // Confirm this with BlamSaveProbe.ps1 -Mode Watch, then set it here.
        // (game_revert is anchor+5, i.e. 0x1357072, if you ever want a revert button.)
        private const int CheckpointRequestDelta = 0;

        // Bytes we expect at KnownEvaluateRva. 0x80 0x3D is "cmp byte ptr [rip+disp32], imm8".
        private static readonly byte[] anchorOpcode = [0x80, 0x3D];

        private const int AnchorInstructionLength = 7;   // 80 3D <disp32> <imm8>

        // P/Invoke goodies
        // Deliberately private and duplicated from MainWindow so that nothing in here can
        // affect the existing games.

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int dwSize, out int lpNumberOfBytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int dwSize, out int lpNumberOfBytesWritten);

        private const int PROCESS_VM_OPERATION = 0x0008;
        private const int PROCESS_WM_READ = 0x0010;
        private const int PROCESS_WM_WRITE = 0x0020;

        // Offset cache
        // Keyed by the sim DLL's file version, so a game update invalidates it by itself.
        // In-memory for this session, and on disk so future launches skip the resolve too.

        private sealed class CachedOffsets
        {
            public string Version { get; set; } = string.Empty;
            public int EvaluateRva { get; set; }
            public int AnchorRva { get; set; }
        }

        private static CachedOffsets? sessionCache;
        private static readonly object cacheLock = new();

        private static string CacheFilePath =>
            Path.Combine(AppContext.BaseDirectory, "campaign-evolved-offsets.json");


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
            Process? process = FindProcess();

            if (process == null)
            {
                message = "Halo: Campaign Evolved is not running.";
                return GameState.NotRunning;
            }

            using (process)
            {
                if (GetModule(process) == null)
                {
                    message = "Halo: Campaign Evolved is running, but you aren't in a mission yet. ";
                    return GameState.RunningButNotInMission;
                }
            }

            message = string.Empty;
            return GameState.Ready;
        }

        /// Actually force a checkpoint. Returns true on success, otherwise false with a reason in
        /// <paramref name="message"/> so the caller can decide whether to show an error window.
        public static bool TryForceCheckpoint(out string message)
        {
            IntPtr processHandle = IntPtr.Zero;
            Process? process = null;

            try
            {
                process = FindProcess();

                if (process == null)
                {
                    message = "Halo: Campaign Evolved is not running.";
                    return false;
                }

                ProcessModule? module = GetModule(process);

                if (module == null)
                {
                    message = "Halo: Campaign Evolved is running, but you aren't in a mission yet.";
                    return false;
                }

                processHandle = OpenProcess(PROCESS_WM_READ | PROCESS_WM_WRITE | PROCESS_VM_OPERATION, false, process.Id);

                if (processHandle == IntPtr.Zero)
                {
                    message = "Couldn't open Halo: Campaign Evolved. You may need to run this program as an administrator, " +
                              "or you're running the Steam version I haven't tested against. Please submit a bug report if necessary.";
                    return false;
                }

                if (!TryGetAnchorRva(processHandle, module, out int anchorRva, out message))
                {
                    return false;
                }

                int offset = anchorRva + CheckpointRequestDelta;
                IntPtr addressToWriteTo = IntPtr.Add(module.BaseAddress, offset);
                byte[] buffer = [1];

                bool result = WriteProcessMemory(processHandle, addressToWriteTo, buffer, buffer.Length, out int bytesWritten);

                if (!result || bytesWritten != buffer.Length)
                {
                    message = "Checkpoint unsuccessfully forced (failed to write to process memory.)";
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
            finally
            {
                if (processHandle != IntPtr.Zero)
                {
                    CloseHandle(processHandle);
                }

                process?.Dispose();
            }
        }

        // The actual offset logic.

        /// Gets the save-globals anchor RVA, doing as little work as possible:
        ///
        ///   1. Already worked it out this session? Use that. (no reads)
        ///   2. On-disk cache matches this DLL version? Verify it and use it. (one 8-byte read)
        ///   3. Hardcoded offset still valid? Use it. (one 8-byte read)
        ///   4. Otherwise the game patched, so resolve it properly. (~7ms, once)
        ///
        /// Steps 1-3 are the normal path and cost essentially nothing. Step 4 happens once
        /// per game update, not once per launch.
        private static bool TryGetAnchorRva(IntPtr processHandle, ProcessModule module, out int anchorRva, out string message)
        {
            message = string.Empty;

            // The sim DLL has no version resource, so FileVersion comes back null. The
            // module's size in memory does change from build to build, so pair the two.
            // This is only an optimisation anyway - anything we pull out of the cache
            // still gets verified below before we use it.
            string version = $"{module.FileVersionInfo.FileVersion ?? "none"}-{module.ModuleMemorySize:X}";

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
                    VerifyAnchor(processHandle, module.BaseAddress, onDisk.EvaluateRva, onDisk.AnchorRva))
                {
                    sessionCache = onDisk;
                    anchorRva = onDisk.AnchorRva;
                    return true;
                }

                // 3. The hardcoded offset from the build this was written against.
                if (VerifyAnchor(processHandle, module.BaseAddress, KnownEvaluateRva, KnownAnchorRva))
                {
                    sessionCache = new CachedOffsets
                    {
                        Version = version,
                        EvaluateRva = KnownEvaluateRva,
                        AnchorRva = KnownAnchorRva
                    };
                    SaveCache(sessionCache);
                    anchorRva = KnownAnchorRva;
                    return true;
                }

                // 4. The game was patched and the offsets moved. Work them out again.
                if (TryResolveAnchor(processHandle, module, out int resolvedEvaluateRva, out int resolvedAnchorRva))
                {
                    sessionCache = new CachedOffsets
                    {
                        Version = version,
                        EvaluateRva = resolvedEvaluateRva,
                        AnchorRva = resolvedAnchorRva
                    };
                    SaveCache(sessionCache);
                    anchorRva = resolvedAnchorRva;
                    return true;
                }

                anchorRva = 0;
                message = "Halo: Campaign Evolved has been updated and the checkpoint offsets no longer " +
                          "match, and they couldn't be worked out automatically. Please report this so " +
                          "the program can be updated.";
                return false;
            }
        }

        /// TL;DR
        /// A sanity check. Reads the 8 bytes at the evaluate proc and confirms it's still
        /// the "cmp byte ptr [rip+disp32], 0" we'd expect, pointing at the flag we'd expect.

        /// This is the bit that matters even if you never use the resolver: without it, a
        /// patch that moves things means writing 1 to an arbitrary byte of the game's memory.
        /// With it, that turns into a clean error message instead.
        /// TL;DR
        private static bool VerifyAnchor(IntPtr processHandle, IntPtr moduleBase, int evaluateRva, int expectedAnchorRva)
        {
            byte[] code = new byte[8];

            if (!ReadProcessMemory(processHandle, IntPtr.Add(moduleBase, evaluateRva), code, code.Length, out int bytesRead) ||
                bytesRead != code.Length)
            {
                return false;
            }

            if (code[0] != anchorOpcode[0] || code[1] != anchorOpcode[1])
            {
                return false;
            }

            int displacement = BitConverter.ToInt32(code, 2);

            return evaluateRva + AnchorInstructionLength + displacement == expectedAnchorRva;
        }

        /// Works out the offsets from scratch. This is not a signature scan of the whole
        /// executable - it's three targeted lookups inside .rdata:
        ///
        ///   1. Find the string "game_saving"
        ///   2. Find the pointer to it (the name field of Blam's hs_function_definition)
        ///   3. read the evaluate proc from that struct and decode its first instruction
        ///
        /// Everything is read out of the live process rather than off disk, which avoids
        /// having to find the game's install directory and avoids the WindowsApps permissions.

        private static bool TryResolveAnchor(IntPtr processHandle, ProcessModule module, out int evaluateRva, out int anchorRva)
        {
            evaluateRva = 0;
            anchorRva = 0;

            IntPtr moduleBase = module.BaseAddress;

            if (!TryFindSection(processHandle, moduleBase, ".rdata", out int rdataRva, out int rdataSize))
            {
                return false;
            }

            byte[] rdata = new byte[rdataSize];

            if (!ReadProcessMemory(processHandle, IntPtr.Add(moduleBase, rdataRva), rdata, rdata.Length, out int read) ||
                read != rdata.Length)
            {
                return false;
            }

            // 1. The string, with the NULs either side so we don't match a longer name.
            byte[] needle = Encoding.ASCII.GetBytes("\0game_saving\0");
            int stringIndex = IndexOf(rdata, needle, 0);

            if (stringIndex < 0)
            {
                return false;
            }

            long stringAddress = moduleBase.ToInt64() + rdataRva + stringIndex + 1;

            // 2. The pointer to it. These are already relocated in memory, so we compare
            //    against the real runtime address rather than the PE's preferred base.
            int pointerIndex = IndexOf(rdata, BitConverter.GetBytes(stringAddress), 0);

            if (pointerIndex < 0 || pointerIndex + 32 > rdata.Length)
            {
                return false;
            }

            // 3. hs_function_definition: +16 is the parse proc (shared by every script
            //    function), +24 is the evaluate proc, which is the one we want.
            long evaluateAddress = BitConverter.ToInt64(rdata, pointerIndex + 24);
            long candidateRva = evaluateAddress - moduleBase.ToInt64();

            if (candidateRva <= 0 || candidateRva >= module.ModuleMemorySize)
            {
                return false;
            }

            byte[] code = new byte[8];

            if (!ReadProcessMemory(processHandle, IntPtr.Add(moduleBase, (int)candidateRva), code, code.Length, out int codeRead) ||
                codeRead != code.Length)
            {
                return false;
            }

            if (code[0] != anchorOpcode[0] || code[1] != anchorOpcode[1])
            {
                return false;
            }

            evaluateRva = (int)candidateRva;
            anchorRva = evaluateRva + AnchorInstructionLength + BitConverter.ToInt32(code, 2);
            return true;
        }


        /// Reads the PE headers out of the mapped image and finds a section by name.
        private static bool TryFindSection(IntPtr processHandle, IntPtr moduleBase, string sectionName, out int sectionRva, out int sectionSize)
        {
            sectionRva = 0;
            sectionSize = 0;

            byte[] headers = new byte[0x1000];

            if (!ReadProcessMemory(processHandle, moduleBase, headers, headers.Length, out int read) || read != headers.Length)
            {
                return false;
            }

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
            int sectionTable = coff + 20 + optionalHeaderSize;

            byte[] wanted = Encoding.ASCII.GetBytes(sectionName.PadRight(8, '\0'));

            for (int i = 0; i < sectionCount; i++)
            {
                int header = sectionTable + (i * 40);

                if (header + 40 > headers.Length)
                {
                    return false;
                }

                bool match = true;

                for (int b = 0; b < 8; b++)
                {
                    if (headers[header + b] != wanted[b])
                    {
                        match = false;
                        break;
                    }
                }

                if (!match)
                {
                    continue;
                }

                sectionSize = BitConverter.ToInt32(headers, header + 8);   // VirtualSize
                sectionRva = BitConverter.ToInt32(headers, header + 12);   // VirtualAddress
                return sectionRva > 0 && sectionSize > 0;
            }

            return false;
        }

        // Other shit.

        private static int IndexOf(byte[] haystack, byte[] needle, int start)
        {
            int last = haystack.Length - needle.Length;

            for (int i = start; i <= last; i++)
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

        private static Process? FindProcess()
        {
            foreach (string name in processNames)
            {
                Process[] found = Process.GetProcessesByName(name);

                if (found.Length > 0)
                {
                    for (int i = 1; i < found.Length; i++)
                    {
                        found[i].Dispose();
                    }

                    return found[0];
                }
            }

            return null;
        }

        private static ProcessModule? GetModule(Process process)
        {
            try
            {
                foreach (ProcessModule module in process.Modules)
                {
                    if (string.Equals(module.ModuleName, ModuleName, StringComparison.OrdinalIgnoreCase))
                    {
                        return module;
                    }
                }
            }
            catch (Exception)
            {
                // Enumerating modules can fail while the game is loading or if we don't have rights
                // to it. Treat that the same as "not in a mission yet."
            }

            return null;
        }

        private static CachedOffsets? LoadCache()
        {
            try
            {
                if (!File.Exists(CacheFilePath))
                {
                    return null;
                }

                return JsonSerializer.Deserialize<CachedOffsets>(File.ReadAllText(CacheFilePath));
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
                File.WriteAllText(CacheFilePath, JsonSerializer.Serialize(offsets));
            }
            catch (Exception)
            {
                // The program is portable, so it might be sat somewhere unwritable. Don't care.
                // We just work the offsets out again next launch, which is ~7ms.
            }
        }
    }
}
