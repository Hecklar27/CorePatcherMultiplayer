using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using CorePatcher.Attributes;
using CorePatcher.Configs;
using log4net;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria;
using Terraria.ModLoader;
using Terraria.ModLoader.UI;
using Terraria.Utilities;
using FieldAttributes = Mono.Cecil.FieldAttributes;

namespace CorePatcher
{
    public class PatchLoader : Loader<ModCorePatch>
    {
        public static int Count => _patchList.Count;

        private static readonly List<ModCorePatch> _patchList = new List<ModCorePatch>();
        private static readonly List<Action> _prePatchList = new List<Action>();
        private static readonly List<Action> _postPatchList = new List<Action>();

        public static void RegisterPrePatchOperation(Action prePatch) => _prePatchList.Add(prePatch);
        public static void RegisterPostPatchOperation(Action postPatch) => _postPatchList.Add(postPatch);
        public static void AddDeps(AssemblyDefinition asmInfo) => PatchDepsEditing.AddDependency(asmInfo);
        public void Register(ModCorePatch patch) => _patchList.Add(patch);

        internal static void PrePatch() { foreach (var action in _prePatchList) action(); }
        internal static void PostPatch() { foreach (var action in _postPatchList) action(); }

        internal static void Apply()
        {
            Console.WriteLine("[CorePatcher] Checking patch status...");

            // 1. Check for Patch Marker in Memory
            if (DetectPatchedAssembly())
            {
                Console.WriteLine("[CorePatcher] Status: Already Patched (Marker found).");
                return;
            }

            // 2. Check execution environment (breaking the restart loop)
            if (IsRunningPatchedExe())
            {
                Console.WriteLine("[CorePatcher] Status: Running 'patched.dll' but marker missing. Skipping patch to prevent lock crash.");
                return;
            }

            // 3. Check for Host & Play scenario: server subprocess with existing patched file in use
            string patchedPath = Path.Combine(Environment.CurrentDirectory, "tModLoader.patched.dll");
            if (IsServerSubprocess(patchedPath))
            {
                Console.WriteLine("[CorePatcher] Status: Server subprocess detected (Host & Play). Patched file exists and is in use by client. Skipping patch.");
                return;
            }

            Console.WriteLine("[CorePatcher] Status: Unpatched. Starting patch process...");

            string originalPath = Path.Combine(Environment.CurrentDirectory, "tModLoader.dll");
            string tempPath = Path.Combine(Environment.CurrentDirectory, "tModLoader.temp.dll");

            // 4. Safe Copy (Read from temp to avoid lock)
            try
            {
                File.Copy(originalPath, tempPath, true);
            }
            catch (Exception e)
            {
                Console.WriteLine($"[CorePatcher] Critical: Failed to create temp file. {e.Message}");
                return;
            }

            // 5. Patching
            using var terrariaAssembly = AssemblyDefinition.ReadAssembly(tempPath, new ReaderParameters(ReadingMode.Immediate) { ReadWrite = false, InMemory = true });

            foreach (var modCorePatch in _patchList)
            {
                var attribute = modCorePatch.GetType().GetCustomAttribute(typeof(PatchType), true);
                if (attribute != null)
                {
                    var typeName = ((PatchType)attribute).GetTypeName();
                    var methods = modCorePatch.GetType().GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
                    foreach (var methodInfo in methods)
                    {
                        var @params = methodInfo.GetParameters();
                        if (@params.Length == 2 && @params[0].ParameterType == typeof(TypeDefinition))
                        {
                            methodInfo.Invoke(modCorePatch, new object[] {
                                terrariaAssembly.MainModule.Types.First(p => p.FullName == typeName),
                                terrariaAssembly
                            });
                        }
                    }
                }
            }

            // 6. Save (With Lock Protection)
            bool saveSuccess = false;
            try
            {
                terrariaAssembly.Write(patchedPath);
                saveSuccess = true;
                Console.WriteLine("[CorePatcher] Patch file written successfully.");
            }
            catch (IOException)
            {
                Console.WriteLine("[CorePatcher] Warning: 'tModLoader.patched.dll' is in use. We are likely already running it.");
                // If we are running it, we assume we are patched enough to continue without crashing.
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CorePatcher] Error writing patch: {ex.Message}");
            }

            // Cleanup
            if (File.Exists(tempPath)) try { File.Delete(tempPath); } catch { }

            if (saveSuccess)
            {
                CopyRuntimeConfig();

                // Only create scripts if configured
                if (ModContent.GetInstance<CorePatcherConfig>().GenerateServerScripts)
                    CreateServerScripts();

                Restart();
            }
        }

        /// <summary>
        /// Detects if we're a server subprocess spawned by Host & Play.
        /// In this scenario:
        /// - We're running with -server flag (or similar server indicators)
        /// - The patched file already exists
        /// - The patched file is locked (in use by the client process)
        /// </summary>
        private static bool IsServerSubprocess(string patchedPath)
        {
            try
            {
                // Must be running as server
                if (!IsServerMode())
                    return false;

                // Patched file must exist
                if (!File.Exists(patchedPath))
                    return false;

                // Check if patched file is locked (client is using it)
                if (IsFileLocked(patchedPath))
                {
                    return true;
                }

                // Also check if the patched file was recently modified (within last few minutes)
                // This catches cases where the file might not be locked but was just created
                var patchedInfo = new FileInfo(patchedPath);
                var timeSinceModified = DateTime.Now - patchedInfo.LastWriteTime;
                if (timeSinceModified.TotalMinutes < 5)
                {
                    // Recently patched, likely a Host & Play scenario
                    return true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CorePatcher] Warning: Error checking server subprocess status: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// Checks if the current process is running in server mode
        /// </summary>
        private static bool IsServerMode()
        {
            try
            {
                // Check command line args for server indicators
                var args = Environment.GetCommandLineArgs();
                if (args.Any(a => a.Equals("-server", StringComparison.OrdinalIgnoreCase)))
                    return true;

                // Check if Main.dedServ is true (dedicated server flag)
                // This field might not be set yet during early loading, so we check args first
                try
                {
                    if (Main.dedServ)
                        return true;
                }
                catch { }

                // Check for other server-related arguments
                if (args.Any(a => a.Equals("-host", StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Checks if a file is currently locked/in use by another process
        /// </summary>
        private static bool IsFileLocked(string filePath)
        {
            try
            {
                // Try to open the file with exclusive access
                using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    // If we get here, file is not locked
                    return false;
                }
            }
            catch (IOException)
            {
                // File is locked
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                // Could also indicate the file is in use
                return true;
            }
            catch
            {
                // Other errors - assume not locked
                return false;
            }
        }

        private static bool IsRunningPatchedExe()
        {
            try
            {
                // Check 1: CLI Args
                var args = Environment.GetCommandLineArgs();
                if (args.Any(a => a.Contains("-corepatched") || a.Contains("tModLoader.patched")))
                    return true;

                // Check 2: AppDomain Friendly Name
                if (AppDomain.CurrentDomain.FriendlyName.Contains("patched"))
                    return true;

                // Check 3: Process Main Module (Windows specific)
                var moduleName = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(moduleName) && moduleName.Contains("patched"))
                    return true;
            }
            catch { }
            return false;
        }

        public static bool DetectPatchedAssembly()
        {
            return typeof(Main).GetField("CorePatched", BindingFlags.Public | BindingFlags.Static) != null;
        }

        private static void CreateServerScripts()
        {
            try
            {
                string batPath = Path.Combine(Environment.CurrentDirectory, "start-patched-server.bat");
                File.WriteAllText(batPath, "@echo off\r\necho Launching CorePatcher...\r\ndotnet tModLoader.dll -server %*\r\npause");

                string shPath = Path.Combine(Environment.CurrentDirectory, "start-patched-server.sh");
                File.WriteAllText(shPath, "#!/bin/sh\n" + "dotnet tModLoader.dll -server \"$@\"");

                if (Environment.OSVersion.Platform == PlatformID.Unix || Environment.OSVersion.Platform == PlatformID.MacOSX)
                    try { Process.Start("chmod", $"+x \"{shPath}\""); } catch { }
            }
            catch { }
        }

        private static void CopyRuntimeConfig()
        {
            string config = Path.Combine(Environment.CurrentDirectory, "tModLoader.runtimeconfig.json");
            if (File.Exists(config))
            {
                File.Copy(config, Path.Combine(Environment.CurrentDirectory, "tModLoader.patched.runtimeconfig.json"), true);
                PatchDepsEditing.PatchTargetRuntime();
            }
        }

        private static void Restart()
        {
            if (!ModContent.GetInstance<CorePatcherConfig>().ReloadUponPatching) return;

            Console.WriteLine("[CorePatcher] Restarting into patched version...");

            // Ensure Steam ID
            string appid = Path.Combine(Environment.CurrentDirectory, "steam_appid.txt");
            if (!File.Exists(appid)) try { File.WriteAllText(appid, "1281930"); } catch { }

            // Pass -corepatched flag to guarantee detection next time
            var args = Environment.GetCommandLineArgs().Skip(1);
            string argString = string.Join(" ", args.Select(a => $"\"{a}\"")) + " -corepatched";

            Process.Start(new ProcessStartInfo("dotnet", $"\"tModLoader.patched.dll\" {argString}")
            {
                WorkingDirectory = Environment.CurrentDirectory,
                UseShellExecute = false
            });

            Thread.Sleep(1000);
            Environment.Exit(0);
        }
    }
}