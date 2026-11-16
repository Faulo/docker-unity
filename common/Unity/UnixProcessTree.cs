using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Unity;

static class UnixProcessTree {
    internal const string INVOCATION_VARIABLE = "UNITY_INVOCATION_ID";
    const int SIG_TERM = 15;
    const int SIG_KILL = 9;
    const int ESRCH = 3;

    [DllImport("libc", SetLastError = true)]
    static extern int pidfd_open(int pid, uint flags);

    [DllImport("libc", SetLastError = true)]
    static extern int pidfd_send_signal(SafeFileHandle descriptor, int signal, IntPtr info, uint flags);

    internal static ChildProcess Start(ProcessStartInfo composer, string invocationId) {
        var startInfo = new ProcessStartInfo("/usr/bin/setsid") { UseShellExecute = false, WorkingDirectory = composer.WorkingDirectory };
        startInfo.Environment.Clear();
        foreach (var variable in composer.Environment) {
            startInfo.Environment[variable.Key] = variable.Value;
        }
        // Unity can create a new session; this identity survives setsid and reparenting.
        startInfo.Environment[INVOCATION_VARIABLE] = invocationId;
        startInfo.ArgumentList.Add("--wait");
        startInfo.ArgumentList.Add(composer.FileName);
        foreach (string argument in composer.ArgumentList) {
            startInfo.ArgumentList.Add(argument);
        }
        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start Composer process group.");
        return new ChildProcess(process, process.Id, null, () => { }, null, invocationId);
    }

    internal static async Task TerminateAsync(ProcessIdentity root, string? invocationId, TimeSpan gracePeriod) {
        var known = new Dictionary<int, long>();
        if (root.IsAlive()) {
            known[root.pid] = root.startMarker;
        }
        var terminated = new HashSet<(int, long)>();
        var elapsed = Stopwatch.StartNew();
        while (true) {
            var owned = Discover(invocationId, known);
            if (owned.Count == 0) {
                return;
            }
            bool force = elapsed.Elapsed >= gracePeriod;
            foreach (var process in owned) {
                if (force || terminated.Add((process.pid, process.startMarker))) {
                    Signal(process, force ? SIG_KILL : SIG_TERM);
                }
            }
            if (elapsed.Elapsed >= gracePeriod + TimeSpan.FromSeconds(2)) {
                throw new TimeoutException("Invocation processes survived termination.");
            }
            await Task.Delay(100);
        }
    }

    static List<Snapshot> Discover(string? invocationId, Dictionary<int, long> known) {
        var all = new List<Snapshot>();
        byte[]? marker = string.IsNullOrEmpty(invocationId) ? null : Encoding.UTF8.GetBytes($"{INVOCATION_VARIABLE}={invocationId}\0");
        foreach (string directory in Directory.EnumerateDirectories("/proc")) {
            if (!int.TryParse(Path.GetFileName(directory), out int pid) || pid == Environment.ProcessId) {
                continue;
            }
            var process = Read(pid);
            if (process is null || process.state == "Z") {
                continue;
            }
            all.Add(process);
            if (marker is not null && HasMarker(pid, marker)) {
                known[pid] = process.startMarker;
            }
        }
        // Include children that clear their environment while their parent is still
        // identifiable. Keep identities after reparenting and never trust PID alone.
        bool added;
        do {
            added = false;
            foreach (var process in all) {
                if (known.TryGetValue(process.pid, out long start) && start == process.startMarker) {
                    continue;
                }
                if (all.Any(parent => parent.pid == process.parent && known.TryGetValue(parent.pid, out long identity) && identity == parent.startMarker)) {
                    known[process.pid] = process.startMarker;
                    added = true;
                }
            }
        } while (added);
        return all.Where(process => known.TryGetValue(process.pid, out long start) && start == process.startMarker).ToList();
    }

    static bool HasMarker(int pid, byte[] marker) {
        try {
            byte[] environment = File.ReadAllBytes($"/proc/{pid}/environ");
            return ContainsMarker(environment, marker);
        } catch (IOException) {
            return false;
        } catch (UnauthorizedAccessException) {
            return false;
        }
    }

    internal static bool ContainsMarker(byte[] environment, byte[] marker) {
        for (int offset = 0; offset < environment.Length;) {
            int end = Array.IndexOf(environment, (byte)0, offset);
            if (end < 0) {
                return false;
            }
            if (environment.AsSpan(offset, end - offset + 1).SequenceEqual(marker)) {
                return true;
            }
            offset = end + 1;
        }
        return false;
    }

    static Snapshot? Read(int pid) {
        try {
            string stat = File.ReadAllText($"/proc/{pid}/stat");
            string[] fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return new Snapshot(pid, int.Parse(fields[1], CultureInfo.InvariantCulture),
                long.Parse(fields[19], CultureInfo.InvariantCulture), fields[0]);
        } catch (IOException) {
            return null;
        } catch (UnauthorizedAccessException) {
            return null;
        }
    }

    static void Signal(Snapshot process, int signal) {
        int descriptor = pidfd_open(process.pid, 0);
        if (descriptor < 0) {
            int error = Marshal.GetLastPInvokeError();
            if (error == ESRCH) {
                return;
            }
            throw new Win32Exception(error, "Cannot open invocation process handle (Linux 5.3 or newer is required).");
        }
        using var handle = new SafeFileHandle(new IntPtr(descriptor), true);
        if (Read(process.pid)?.startMarker != process.startMarker) {
            return;
        }
        // The handle stays bound to this process even if its PID is reused.
        if (pidfd_send_signal(handle, signal, IntPtr.Zero, 0) != 0) {
            int error = Marshal.GetLastPInvokeError();
            if (error != ESRCH) {
                throw new Win32Exception(error, "Cannot signal invocation process.");
            }
        }
    }

    sealed record Snapshot(int pid, int parent, long startMarker, string state);
}
