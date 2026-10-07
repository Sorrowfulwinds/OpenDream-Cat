using System.Collections.Generic;
using DMCompiler.Json;

namespace DMDisassembler;

internal class DMType {
    public DMProc? InitProc;
    public DreamTypeJson Json;
    public string Path;
    public Dictionary<string, DMProc[]> Procs;

    public DMType(DreamTypeJson json) {
        Json = json;
        Path = Json.Path;

        InitProc = Json.InitProc.HasValue ? Program.Procs[Json.InitProc.Value] : null;

        Procs = new Dictionary<string, DMProc[]>(json.Procs?.Count ?? 0);
        if (Json.Procs != null)
            foreach (List<int> procIds in Json.Procs) {
                var procs = new DMProc[procIds.Count];
                for (var i = 0; i < procIds.Count; i++) procs[i] = Program.Procs[procIds[i]];

                Procs.Add(procs[0].Name, procs);
            }
    }
}
