// Headless port of Il2CppDumper's ghidra_with_struct.py: parse il2cpp_ghidra.h, then apply
// method names, string literals, metadata, function starts and signatures from script.json.
// Args: <il2cpp_ghidra.h> <script.json>
import ghidra.app.script.GhidraScript;
import ghidra.app.cmd.function.ApplyFunctionSignatureCmd;
import ghidra.app.util.cparser.C.CParser;
import ghidra.app.util.cparser.C.CParserUtils;
import ghidra.program.model.address.Address;
import ghidra.program.model.data.*;
import ghidra.program.model.symbol.SourceType;
import com.google.gson.*;
import java.io.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.util.*;

public class ApplyIl2CppDumper extends GhidraScript {
    Address base;

    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        base = currentProgram.getImageBase();

        println("Parsing header " + args[0]);
        DataTypeManager dtm = currentProgram.getDataTypeManager();
        CParser parser = new CParser(dtm, true, null);
        parser.parse(new String(Files.readAllBytes(Paths.get(args[0])), StandardCharsets.UTF_8));
        println("Header parsed");

        JsonObject data;
        try (Reader r = new InputStreamReader(new FileInputStream(args[1]), StandardCharsets.UTF_8)) {
            data = JsonParser.parseReader(r).getAsJsonObject();
        }

        JsonArray methods = data.getAsJsonArray("ScriptMethod");
        monitor.setMessage("Methods");
        for (JsonElement e : methods) {
            JsonObject m = e.getAsJsonObject();
            label(addr(m.get("Address").getAsLong()), m.get("Name").getAsString());
        }

        int i = 0;
        monitor.setMessage("Strings");
        for (JsonElement e : data.getAsJsonArray("ScriptString")) {
            JsonObject s = e.getAsJsonObject();
            Address a = addr(s.get("Address").getAsLong());
            i++;
            try { createLabel(a, "StringLiteral_" + i, true, SourceType.USER_DEFINED); setEOLComment(a, s.get("Value").getAsString()); }
            catch (Exception ex) { }
        }

        monitor.setMessage("Metadata");
        for (JsonElement e : data.getAsJsonArray("ScriptMetadata")) {
            JsonObject md = e.getAsJsonObject();
            Address a = addr(md.get("Address").getAsLong());
            String name = md.get("Name").getAsString();
            label(a, name);
            setEOLComment(a, name);
            JsonElement sig = md.get("Signature");
            if (sig != null && !sig.isJsonNull() && !sig.getAsString().isEmpty()) setType(a, sig.getAsString());
        }

        monitor.setMessage("Metadata methods");
        for (JsonElement e : data.getAsJsonArray("ScriptMetadataMethod")) {
            JsonObject mm = e.getAsJsonObject();
            Address a = addr(mm.get("Address").getAsLong());
            String name = mm.get("Name").getAsString();
            label(a, name);
            setEOLComment(a, name);
        }

        monitor.setMessage("Functions");
        JsonArray addresses = data.getAsJsonArray("Addresses");
        for (int k = 0; k < addresses.size() - 1; k++) {
            Address a = addr(addresses.get(k).getAsLong());
            if (getFunctionAt(a) == null) {
                try { createFunction(a, null); } catch (Exception ex) { }
            }
        }

        monitor.setMessage("Signatures");
        int failed = 0;
        for (JsonElement e : methods) {
            JsonObject m = e.getAsJsonObject();
            String sig = m.get("Signature").getAsString();
            if (!setSig(addr(m.get("Address").getAsLong()), m.get("Name").getAsString(), sig.substring(0, sig.length() - 1))) failed++;
        }
        println("Done; signatures not applied: " + failed);
    }

    Address addr(long offset) { return base.add(offset); }

    void label(Address a, String name) {
        try { createLabel(a, name.replace(' ', '-'), true, SourceType.USER_DEFINED); } catch (Exception ex) { }
    }

    void setType(Address a, String typeStr) {
        String t = typeStr.replace("*", " *").replace("  ", " ").trim();
        DataType[] found = getDataTypes(t);
        DataType dt = null;
        if (found.length == 1) dt = found[0];
        else if (found.length == 0 && t.endsWith(" *")) {
            DataType[] b = getDataTypes(t.substring(0, t.length() - 2));
            if (b.length == 1) dt = currentProgram.getDataTypeManager().getPointer(b[0]);
        }
        if (dt == null) return;
        try { createData(a, dt); } catch (Exception ex) { }
    }

    boolean setSig(Address a, String name, String sig) {
        FunctionDefinitionDataType fd = null;
        try { fd = CParserUtils.parseSignature((ghidra.app.services.DataTypeManagerService) null, currentProgram, sig, false); }
        catch (Exception ex) {
            try { fd = CParserUtils.parseSignature((ghidra.app.services.DataTypeManagerService) null, currentProgram,
                    sig.replace(", ", "ext, ").replace(")", "ext)"), false); }
            catch (Exception ex2) { return false; }
        }
        if (fd == null) return false;
        try {
            fd.setName(name);
            return new ApplyFunctionSignatureCmd(a, fd, SourceType.USER_DEFINED, false, true).applyTo(currentProgram);
        }
        catch (Exception ex) { return false; }
    }
}
