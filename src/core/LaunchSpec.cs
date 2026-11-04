using System.Collections.Generic;
using System.IO;

// What to start (SPEC §6). Built only here.
public class LaunchSpec
{
    public string FileName;
    public string Arguments;
    public string MaskedArguments;  // for logs and self-test
    public string WorkingDirectory;
    public Dictionary<string, string> Environment = new Dictionary<string, string>();  // added to inherited env

    public static LaunchSpec Build(ResolvedLaunch r, string[] agentArgs, string systemRoot)
    {
        var spec = new LaunchSpec();
        spec.WorkingDirectory = r.LaunchDir;
        if (!r.IsWsl)
        {
            spec.FileName = r.Executable;
            spec.Arguments = spec.MaskedArguments = ArgQuoter.Join(agentArgs);
            foreach (var kv in r.Env) spec.Environment[kv.Key] = kv.Value;
            return spec;
        }

        spec.FileName = Path.Combine(Path.Combine(systemRoot, "System32"), "wsl.exe");
        spec.Arguments = ArgQuoter.Join(BuildWslArguments(r, agentArgs, false));
        spec.MaskedArguments = ArgQuoter.Join(BuildWslArguments(r, agentArgs, true));
        spec.Environment["WSL_UTF8"] = "1";  // wsl.exe's own messages as UTF-8, not UTF-16 (D10)
        return spec;
    }

    // Variant A (SPEC §6.1): -d <distro> --cd <linuxCwd> --exec [env K=V …] <agentExe> <args…>
    static List<string> BuildWslArguments(ResolvedLaunch r, string[] agentArgs, bool mask)
    {
        var a = new List<string> { "-d", r.Distribution, "--cd", r.LinuxCwd, "--exec" };
        if (r.Env.Count > 0)
        {
            a.Add("env");
            foreach (var kv in r.Env)
                a.Add(kv.Key + "=" + (mask ? RelayLog.Mask(kv.Key, kv.Value) : kv.Value));
        }
        a.Add(r.Executable);
        a.AddRange(agentArgs);
        return a;
    }
}
