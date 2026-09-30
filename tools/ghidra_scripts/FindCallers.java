import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;
import java.io.*;
import java.util.*;

public class FindCallers extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();          // outFile name
        String name = args[1];
        FunctionManager fm = currentProgram.getFunctionManager();
        StringBuilder sb = new StringBuilder();

        for (Function target : fm.getFunctions(true)) {
            if (!target.getName().contains(name)) continue;
            sb.append("== callers of ").append(target.getName()).append("\n");
            Set<String> seen = new TreeSet<>();
            for (Reference ref : getReferencesTo(target.getEntryPoint())) {
                Function caller = fm.getFunctionContaining(ref.getFromAddress());
                seen.add(caller != null ? caller.getName() : ref.getFromAddress().toString());
            }
            for (String s : seen) sb.append("  ").append(s).append("\n");
        }
        try (PrintWriter w = new PrintWriter(args[0])) { w.print(sb); }
    }
}