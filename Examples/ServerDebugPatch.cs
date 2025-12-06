using CorePatcher.Attributes;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using System;
using System.Linq;
using Terraria.ModLoader;

namespace CorePatcher.Examples
{
    // We target Terraria.Main because that holds the Server loop and the Version string
    [PatchType("Terraria.Main")]
    internal class ServerDebugPatch : ModCorePatch
    {
        /// <summary>
        /// VISUAL CHECK 1: Modifies the version string.
        /// - Client: Visible on bottom left of Main Menu.
        /// - Server: Visible in startup logs.
        /// </summary>
        private static void PatchVersionString(TypeDefinition type, AssemblyDefinition terraria)
        {
            // 1. Find the static constructor (.cctor) where versionNumber is set
            var cctor = type.Methods.FirstOrDefault(m => m.Name == ".cctor");
            if (cctor == null) return;

            // 2. Import the String.Concat method so we can join strings in IL
            var concatMethod = typeof(string).GetMethod("Concat", new[] { typeof(string), typeof(string) });
            var concatRef = terraria.MainModule.ImportReference(concatMethod);

            // 3. Find the versionNumber field
            var versionField = type.Fields.FirstOrDefault(f => f.Name == "versionNumber");
            if (versionField == null) return;

            // 4. Setup the cursor to edit the End of the method (before it returns)
            ILCursor cursor = new ILCursor(new ILContext(cctor));

            // Go to the last instruction (usually Ret)
            while (cursor.TryGotoNext(MoveType.Before, i => i.MatchRet())) { /* loop to end */ }

            // If we are at the end, verify we aren't null
            if (cursor.Instrs.Count > 0)
            {
                // Move back before the Ret instruction
                cursor.Goto(cursor.Instrs.Count - 1);

                // 5. Inject: versionNumber = versionNumber + " [Core Patcher Active]";
                cursor.Emit(OpCodes.Ldsfld, versionField);          // Load current version ("v1.4.4.9")
                cursor.Emit(OpCodes.Ldstr, " [Core Patcher Active]"); // Load our tag
                cursor.Emit(OpCodes.Call, concatRef);               // Combine them
                cursor.Emit(OpCodes.Stsfld, versionField);          // Save back to field
            }
        }

        /// <summary>
        /// VISUAL CHECK 2: Prints a giant banner to the Server Console.
        /// Only runs on Dedicated Servers.
        /// </summary>
        private static void PatchServerConsole(TypeDefinition type, AssemblyDefinition terraria)
        {
            // 1. Find the DedServ method (The main loop for Dedicated Server)
            var method = type.Methods.FirstOrDefault(m => m.Name == "DedServ");
            if (method == null) return;

            // 2. Import Console.WriteLine
            var writeLine = typeof(Console).GetMethod("WriteLine", new[] { typeof(string) });
            var writeLineRef = terraria.MainModule.ImportReference(writeLine);

            // 3. Setup Cursor at the VERY START of the method
            ILCursor cursor = new ILCursor(new ILContext(method));
            cursor.Goto(0);

            // 4. Emit Console.WriteLine calls
            string banner = "\n" +
                            "##########################################\n" +
                            "#       CORE PATCHER IS RUNNING          #\n" +
                            "#   Server Patches applied successfully  #\n" +
                            "##########################################\n";

            cursor.Emit(OpCodes.Ldstr, banner);
            cursor.Emit(OpCodes.Call, writeLineRef);
        }
    }
}