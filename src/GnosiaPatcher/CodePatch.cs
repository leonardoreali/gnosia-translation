using Mono.Cecil;
using Mono.Cecil.Cil;

namespace GnosiaPatcher;

/// <summary>
/// The Doctor's and Engineer's reports ("{1} {3} {0}.") are finished by the game's code, which fills {3}, {4} and
/// {5} with the English words "was", "were", "wasn't" and "weren't", choosing the plural when the list of names
/// contains "and". This rewrites those string constants in Assembly-CSharp.dll with the translation's
/// "Code/was", "Code/wasn't", "Code/were" and "Code/weren't" entries, and "and" with its own word for "and".
/// </summary>
static class CodePatch
{
    const string Dll = "Assembly-CSharp.dll";
    const string BackupSuffix = ".original";

    /// <summary>Text row the game puts between names ("Stella and Gina").</summary>
    const string AndKey = "ScenarioBaseText/4:Jinro/69";

    static readonly string[] Words = { "was", "wasn't", "were", "weren't" };

    /// <summary>Patches the report methods from the backup of the original DLL. Returns the number of strings replaced.</summary>
    public static int Apply(string dataDir, TranslationFile translation)
    {
        var replace = new Dictionary<string, string>();
        foreach (var word in Words)
            if (translation.Entries.TryGetValue("Code/" + word, out var text)) replace[word] = text;
        if (replace.Count == 0) { Restore(dataDir); return 0; }
        if (translation.Entries.TryGetValue(AndKey, out var and) && and.Trim().Length > 0)
            replace["and"] = " " + and.Trim() + " ";

        string path = Path.Combine(dataDir, "Managed", Dll), backup = path + BackupSuffix;
        if (!File.Exists(backup)) File.Copy(path, backup);

        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(path)!);
        int changed = 0;
        using (var asm = AssemblyDefinition.ReadAssembly(backup, new ReaderParameters { AssemblyResolver = resolver }))
        {
            // only the methods that build a report: they are the ones loading "wasn't"
            var methods = asm.MainModule.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody &&
                m.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == "wasn't"));
            foreach (var method in methods)
                foreach (var ins in method.Body.Instructions)
                    if (ins.OpCode == OpCodes.Ldstr && replace.TryGetValue((string)ins.Operand, out var text))
                    {
                        ins.Operand = text;
                        changed++;
                    }
            asm.Write(path + ".tmp");
        }
        File.Move(path + ".tmp", path, overwrite: true);
        return changed;
    }

    /// <summary>Puts the original DLL back. Returns false when it was not patched.</summary>
    public static bool Restore(string dataDir)
    {
        string path = Path.Combine(dataDir, "Managed", Dll), backup = path + BackupSuffix;
        if (!File.Exists(backup)) return false;
        File.Copy(backup, path, overwrite: true);
        File.Delete(backup);
        return true;
    }
}
