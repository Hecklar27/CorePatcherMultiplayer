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

        public static void RegisterPrePatchOperation(Action prePatch)
        {
            _prePatchList.Add(prePatch);
        }

        public static void RegisterPostPatchOperation(Action postPatch)
        {
            _postPatchList.Add(postPatch);
        }

        public static void AddDeps(AssemblyDefinition asmInfo)
        {
            PatchDepsEditing.AddDependency(asmInfo);
        }

        public void Register(ModCorePatch patch)
        {
            _patchList.Add(patch);
        }

        internal static void PrePatch()
        {
            foreach (var action in _prePatchList)
            {
                action();
            }
        }

        internal static void PostPatch()
        {
            foreach (var action in _postPatchList)
            {
                action();
            }
        }

        internal static void Apply()
        {
            // 1. Check if the currently running assembly is already patched
            if (DetectPatchedAssembly())
            {
                return;
            }

            string originalPath = Path.Combine(Environment.CurrentDirectory, "tModLoader.dll");
            string backupPath = Path.Combine(Environment.CurrentDirectory, "tModLoader.vanilla.dll");
            string patchedPath = Path.Combine(Environment.CurrentDirectory, "tModLoader.patched.dll");

            // 2. Load the current assembly into memory
            // We read from the current executable on disk
            using var terrariaAssembly = AssemblyDefinition.ReadAssembly(originalPath, new ReaderParameters(ReadingMode.Immediate)
            {
                ReadWrite = true,
                InMemory = true
            });

            // 3. Apply Patches
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
                        if (@params.Length == 2 &&
                            @params[0].ParameterType == typeof(TypeDefinition) &&
                            @params[1].ParameterType == typeof(AssemblyDefinition))
                        {
                            methodInfo.Invoke(modCorePatch, new object[] {
                                terrariaAssembly.MainModule.Types.First(p => p.FullName == typeName),
                                terrariaAssembly
                            });
                        }
                    }
                }
            }

            // THE SWAP (Fixes Host & Play)
            try
            {
                if (!File.Exists(backupPath))
                {
                    if (File.Exists(originalPath))
                    {
                        File.Move(originalPath, backupPath); 
                    }
                }

                terrariaAssembly.Write(originalPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CorePatcher] Critical Error swapping files: {ex.Message}");
                // Fallback: Try to write to .patched.dll if the swap failed
                try { terrariaAssembly.Write(patchedPath); } catch { }
            }

            CopyRuntimeConfig();

            // Create the start script (Optional, but useful for dedicated servers)
            // We update this to point to the main DLL now.
            if (ModContent.GetInstance<CorePatcherConfig>().GenerateServerScripts)
            {
                string batPath = Path.Combine(Environment.CurrentDirectory, "start-patched-server.bat");
                File.WriteAllText(batPath, "dotnet tModLoader.dll -server %*");
            }

            Restart();
        }

        private static void Restart()
        {
            if (!ModContent.GetInstance<CorePatcherConfig>().ReloadUponPatching)
            {
                return;
            }

            // Ensure Steam ID exists
            string appidPath = Path.Combine(Environment.CurrentDirectory, "steam_appid.txt");
            if (!File.Exists(appidPath)) try { File.WriteAllText(appidPath, "1281930"); } catch { }

            // Capture launch args
            var args = Environment.GetCommandLineArgs().Skip(1);
            string argString = string.Join(" ", args.Select(a => $"\"{a}\""));

            // LAUNCH THE MAIN FILE (Now Patched)
            Process process = new Process();
            process.StartInfo = new ProcessStartInfo("dotnet", $"\"tModLoader.dll\" {argString}")
            {
                WorkingDirectory = Environment.CurrentDirectory,
                UseShellExecute = false
            };

            process.Start();
            Thread.Sleep(1000);
            Environment.Exit(0);
        }

        public static bool DetectPatchedAssembly()
        {
            FieldInfo CorePatchedFieldInfo =
                typeof(Main).GetField("CorePatched", BindingFlags.Public | BindingFlags.Static);
            return CorePatchedFieldInfo != null;
        }

        private static void DeleteOnceDone(object sender, EventArgs e)
        {
            if (DetectPatchedAssembly())
            {
                string runtimeConfigPath = Path.Combine(Environment.CurrentDirectory, "tModLoader.runtimeconfig.json");
                string runtimeConfigDev = Path.Combine(Environment.CurrentDirectory, "tModLoader.runtimeconfig.dev.json");
                if (File.Exists(runtimeConfigPath))
                {

                    File.Delete(Path.Combine(Environment.CurrentDirectory, "tModLoader.patched.runtimeconfig.json"));
                    File.Delete(Path.Combine(Environment.CurrentDirectory, "tModLoader.patched.runtimeconfig.dev.json"));
                }
            }
        }

        private static void CopyRuntimeConfig()
        {
            string runtimeConfigPath = Path.Combine(Environment.CurrentDirectory, "tModLoader.runtimeconfig.json");
            string runtimeConfigDev = Path.Combine(Environment.CurrentDirectory, "tModLoader.runtimeconfig.dev.json");
            if (File.Exists(runtimeConfigPath))
            {

                File.Copy(runtimeConfigPath, Path.Combine(Environment.CurrentDirectory, "tModLoader.patched.runtimeconfig.json"), true);
                File.Copy(runtimeConfigDev, Path.Combine(Environment.CurrentDirectory, "tModLoader.patched.runtimeconfig.dev.json"), true);
                PatchDepsEditing.PatchTargetRuntime();
            }
        }

        private static void CreateServerScripts()
        {
            try
            {
                string patchedDll = "tModLoader.patched.dll";

                // Windows Batch Script
                string batPath = Path.Combine(Environment.CurrentDirectory, "start-patched-server.bat");
                string batContent = $"@echo off\r\n" +
                                    $"echo Launching CorePatcher Server...\r\n" +
                                    $"dotnet \"{patchedDll}\" -server %*\r\n" +
                                    $"pause";
                File.WriteAllText(batPath, batContent);

                // Linux/Mac Shell Script
                string shPath = Path.Combine(Environment.CurrentDirectory, "start-patched-server.sh");
                string shContent = $"#!/bin/sh\n" +
                                   $"echo \"Launching CorePatcher Server...\"\n" +
                                   $"dotnet \"{patchedDll}\" -server \"$@\"";
                File.WriteAllText(shPath, shContent);

                // Attempt chmod +x for Mac/Linux
                if (Environment.OSVersion.Platform == PlatformID.Unix || Environment.OSVersion.Platform == PlatformID.MacOSX)
                {
                    try { Process.Start("chmod", $"+x \"{shPath}\""); } catch { }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"[CorePatcher] Failed to create server scripts: {e.Message}");
            }
        }

        [PatchType("Terraria.ModLoader.UI.Interface")]
        internal class InterfacePatch : ModCorePatch
        {
            private static void ModifyModLoaderMenus(TypeDefinition type, AssemblyDefinition terraria)
            {
                FieldDefinition definition =
                new FieldDefinition("CorePatched", FieldAttributes.Public | FieldAttributes.Static, type.Module.TypeSystem.Boolean);
                var main = terraria.MainModule.Types.First(i => i.FullName == "Terraria.Main").Fields;
                main.Add(definition);
                EditStaticFieldString(definition);

                if (!ModContent.GetInstance<CorePatcherConfig>().DevMode) return;

                FieldReference infoMessage = terraria.MainModule.Types.First(i => i.FullName == "Terraria.ModLoader.UI.Interface").Fields.First(i => i.Name == "infoMessage");
                FieldReference menuMode = terraria.MainModule.Types.First(i => i.FullName == "Terraria.Main").Fields.First(i => i.Name == "menuMode");

                FieldReference corePatcher = terraria.MainModule.Types.First(i => i.FullName == "Terraria.Main").Fields.FirstOrDefault(i => i.Name == "CorePatched");

                MethodReference show = terraria.MainModule.Types.First(i => i.FullName == "Terraria.ModLoader.UI.UIInfoMessage").Methods.First(i => i.Name == "Show");

                var method = type.Methods.First(i => i.Name == "ModLoaderMenus");

                var instructions = method.Body.GetILProcessor().Body.Instructions;

                ILContext context = new ILContext(method);
                ILCursor cursor = new ILCursor(context);

                Instruction target = cursor.Instrs[cursor.Index + 3];
                Instruction target2 = cursor.Instrs[2];
                instructions.Insert(3, Instruction.Create(OpCodes.Brtrue, target));
                instructions.Insert(3, Instruction.Create(OpCodes.Ldsfld, corePatcher));

                Instruction instruction = Instruction.Create(OpCodes.Br, (Instruction)target2.Operand);

                cursor.Index += 5;

                cursor.EmitLdcI4(1);
                cursor.EmitStsfld(corePatcher);

                cursor.Emit(OpCodes.Ldsfld, infoMessage);
                cursor.EmitLdstr(BuildMessage());
                cursor.EmitLdsfld(menuMode);
                cursor.EmitLdnull();
                cursor.EmitLdstr("");
                cursor.EmitLdnull();
                cursor.EmitLdnull();
                cursor.EmitCallvirt(show);

                instructions.Insert(cursor.Index, instruction);
            }

            private static string BuildMessage()
            {
                StringBuilder builder = new StringBuilder();
                builder.AppendLine("Welcome to tModLoader - Core patcher dev mode!");
                builder.AppendLine("If you see this, this mean you have the dev mode option enabled in the config for Core patcher.");
                builder.AppendLine("Here are a couple tips to help you in your journey through core modding!");
                builder.AppendLine();
                builder.AppendLine("=== View your patches ===");
                builder.AppendLine("1. Open ILSpy, DNSpy or your favorite program to view C# assembly IL/Code");
                builder.AppendLine("2. Go in your tML installation folder");
                builder.AppendLine("3. Drag and drop the tModLoader.patched.dll into ILSpy");
                builder.AppendLine("4. Go to the method/class where you have done your patches.");
                builder.AppendLine();
                builder.AppendLine("=== Debugging your mod with tModLoader - core patcher (Require VS) ===");
                builder.AppendLine("0. Stay on this screen");
                builder.AppendLine("1. In VS with your mod project opened go in the Debugging tabs and click on \"Attach to process\" (or press CTRL+ALT+P)");
                builder.AppendLine("2. In the process list, find a dotnet.exe process with tmodloader as the title.");
                builder.AppendLine("3. Click on attach and it's done!");
                builder.AppendLine();
                builder.AppendLine("Thanks for using core patcher!");
                return builder.ToString();
            }

            private static void DelegateToInject()
            {
                //Interface.infoMessage.Show("This is a test message", Main.menuMode);
            }

            private static void EditStaticFieldString(FieldDefinition definition)
            {
                MethodDefinition staticConstructor = definition.DeclaringType.Methods.FirstOrDefault(m => m.Name == ".cctor");

                if (staticConstructor != null)
                {
                    ILProcessor processor = staticConstructor.Body.GetILProcessor();

                    IList<Instruction> instructions = new List<Instruction>();
                    instructions.Add(processor.Create(OpCodes.Ldc_I4, 0));
                    instructions.Add(processor.Create(OpCodes.Stsfld, definition));
                    foreach (Instruction instruction in instructions)
                    {
                        processor.Body.Instructions.Insert(processor.Body.Instructions.Count - 2, instruction);
                    }
                }
            }
        }
    }
}