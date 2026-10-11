// Headless post-script: decompile every function to <outdir>/<program>.c
// Usage: analyzeHeadless ... -postScript ExportDecompiled.java <outdir>
import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.Function;

import java.io.File;
import java.io.PrintWriter;

public class ExportDecompiled extends GhidraScript {
    @Override
    protected void run() throws Exception {
        String outDir = getScriptArgs().length > 0 ? getScriptArgs()[0] : ".";
        File out = new File(outDir, currentProgram.getName() + ".c");
        DecompInterface ifc = new DecompInterface();
        ifc.openProgram(currentProgram);
        int n = 0;
        try (PrintWriter w = new PrintWriter(out, "UTF-8")) {
            for (Function f : currentProgram.getFunctionManager().getFunctions(true)) {
                if (f.isExternal() || f.isThunk()) {
                    continue;
                }
                DecompileResults r = ifc.decompileFunction(f, 120, monitor);
                w.println("// ==== " + f.getName(true) + " @ " + f.getEntryPoint());
                if (r != null && r.decompileCompleted()) {
                    w.println(r.getDecompiledFunction().getC());
                } else {
                    w.println("// decompile failed: " + (r == null ? "null" : r.getErrorMessage()));
                }
                n++;
            }
        }
        println("Exported " + n + " functions to " + out);
    }
}
