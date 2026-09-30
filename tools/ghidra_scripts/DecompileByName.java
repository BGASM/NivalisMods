import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.listing.*;
import java.io.*;

public class DecompileByName extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();          // outDir name1 name2 ...
        File outDir = new File(args[0]);
        outDir.mkdirs();

        DecompInterface d = new DecompInterface();
        DecompileOptions opts = new DecompileOptions();
        opts.grabFromProgram(currentProgram);
        d.setOptions(opts);
        d.openProgram(currentProgram);

        for (int i = 1; i < args.length; i++) {
            String name = args[i];
            StringBuilder sb = new StringBuilder();
            int found = 0;

            for (Function f : currentProgram.getFunctionManager().getFunctions(true)) {
                if (!f.getName().contains(name)) continue;
                found++;
                DecompileResults r = d.decompileFunction(f, 120, monitor);
                sb.append("// ").append(f.getName()).append(" @ ").append(f.getEntryPoint()).append("\n");
                sb.append(r.decompileCompleted()
                    ? r.getDecompiledFunction().getC()
                    : "// decompile failed: " + r.getErrorMessage()).append("\n\n");
            }
            if (found == 0) sb.append("// no function matching ").append(name).append("\n");

            String file = name.replaceAll("[^A-Za-z0-9_]", "_") + ".c";
            try (PrintWriter w = new PrintWriter(new File(outDir, file))) { w.print(sb); }
            println("wrote " + file + " (" + found + " match)");
        }
    }
}