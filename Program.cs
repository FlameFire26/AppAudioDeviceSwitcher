using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

return ProgramEntry.Run(args);

internal static class ProgramEntry
{
    public static int Run(string[] args)
    {
        try
        {
            if (args.Length == 0 || IsHelp(args[0]))
            {
                PrintUsage();
                return 0;
            }

            using var audio = new AudioEnvironment();
            return args[0].ToLowerInvariant() switch
            {
                "list-devices" => RunListDevices(audio),
                "list-apps" => RunListApps(audio, args.Skip(1).ToArray()),
                "resolve" => RunResolve(audio, args.Skip(1).ToArray()),
                "set" => RunSet(audio, args.Skip(1).ToArray()),
                "get" => RunGet(audio, args.Skip(1).ToArray()),
                "clear" => RunClear(audio, args.Skip(1).ToArray()),
                _ => Fail($"Unknown command: {args[0]}")
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static int RunListDevices(AudioEnvironment audio)
    {
        var defaultDevice = audio.TryGetDefaultRenderDevice();
        foreach (var device in audio.GetRenderDevices())
        {
            var marker = device.ID == defaultDevice?.ID ? "*" : " ";
            Console.WriteLine($"{marker} {device.FriendlyName}");
            Console.WriteLine($"  DeviceId : {device.ID}");
            Console.WriteLine($"  State    : {device.State}");
            Console.WriteLine();
        }

        return 0;
    }

    private static int RunListApps(AudioEnvironment audio, string[] args)
    {
        var includeInactive = args.Any(static arg => arg.Equals("--all", StringComparison.OrdinalIgnoreCase));
        var sessions = audio.GetRenderSessions(includeInactive).ToList();

        if (sessions.Count == 0)
        {
            Console.WriteLine("No matching audio application sessions were found.");
            return 0;
        }

        foreach (var session in sessions
            .OrderBy(static s => s.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static s => s.ProcessId)
            .ThenBy(static s => s.DeviceName, StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine($"{session.ProcessName} (PID {session.ProcessId})");
            Console.WriteLine($"  SessionState    : {session.State}");
            Console.WriteLine($"  CurrentDevice   : {session.DeviceName}");
            Console.WriteLine($"  CurrentDeviceId : {session.DeviceId}");
            Console.WriteLine($"  PersistedRoute  : {session.PersistedDeviceId ?? "(default device)"}");

            if (!string.IsNullOrWhiteSpace(session.ExecutablePath))
            {
                Console.WriteLine($"  Executable      : {session.ExecutablePath}");
            }

            Console.WriteLine();
        }

        return 0;
    }

    private static int RunSet(AudioEnvironment audio, string[] args)
    {
        if (args.Length < 2)
        {
            return Fail("Usage: AppAudioDeviceSwitcher set <app-or-pid> <device-id>");
        }

        var selector = NormalizeInputArgument(args[0]);
        var deviceId = NormalizeInputArgument(args[1]);
        if (!audio.DeviceExists(deviceId))
        {
            return Fail($"Device not found: {deviceId}");
        }

        var targets = audio.ResolveTargets(selector).ToList();
        if (targets.Count == 0)
        {
            return Fail($"No process or audio session matched '{selector}'.");
        }

        var routed = new HashSet<int>();
        foreach (var target in targets)
        {
            if (routed.Add(target.ProcessId))
            {
                TrySetRenderDevice(audio, target.ProcessId, target.ProcessName, deviceId);
                Console.WriteLine($"Set PID {target.ProcessId} ({target.ProcessName}) -> {deviceId}");
            }
        }

        Console.WriteLine("Note: existing audio streams may need to recreate their session before the new route is audible.");
        return 0;
    }

    private static int RunResolve(AudioEnvironment audio, string[] args)
    {
        if (args.Length < 1)
        {
            return Fail("Usage: AppAudioDeviceSwitcher resolve <app-or-pid>");
        }

        var resolution = audio.ResolveSelector(args[0]);
        Console.WriteLine($"RawInput         : {args[0]}");
        Console.WriteLine($"NormalizedInput  : {resolution.NormalizedSelector}");
        Console.WriteLine($"ResolutionKind   : {resolution.ResolutionKind}");

        if (resolution.WindowHandle != IntPtr.Zero)
        {
            Console.WriteLine($"WindowHandle     : {resolution.WindowHandle} (0x{resolution.WindowHandle.ToInt64():X})");
        }

        if (resolution.ResolvedProcessId is int resolvedProcessId)
        {
            Console.WriteLine($"ResolvedPid      : {resolvedProcessId}");
        }

        if (resolution.Targets.Count == 0)
        {
            Console.WriteLine("Matches          : (none)");
            return 0;
        }

        Console.WriteLine("Matches          :");
        foreach (var target in resolution.Targets)
        {
            Console.WriteLine($"  PID {target.ProcessId} ({target.ProcessName})");
        }

        return 0;
    }

    private static int RunGet(AudioEnvironment audio, string[] args)
    {
        if (args.Length < 1)
        {
            return Fail("Usage: AppAudioDeviceSwitcher get <app-or-pid>");
        }

        var selector = NormalizeInputArgument(args[0]);
        var targets = audio.ResolveTargets(selector).ToList();
        if (targets.Count == 0)
        {
            return Fail($"No process or audio session matched '{selector}'.");
        }

        foreach (var target in targets.DistinctBy(static target => target.ProcessId))
        {
            var persistedDeviceId = audio.Policy.GetRenderDeviceForProcess(target.ProcessId);
            Console.WriteLine($"{target.ProcessName} (PID {target.ProcessId})");
            Console.WriteLine($"  PersistedRoute  : {persistedDeviceId ?? "(default device)"}");

            if (!string.IsNullOrWhiteSpace(persistedDeviceId))
            {
                var device = audio.TryGetRenderDevice(persistedDeviceId);
                if (device != null)
                {
                    Console.WriteLine($"  DeviceName      : {device.FriendlyName}");
                }
            }

            Console.WriteLine();
        }

        return 0;
    }

    private static int RunClear(AudioEnvironment audio, string[] args)
    {
        if (args.Length < 1)
        {
            return Fail("Usage: AppAudioDeviceSwitcher clear <app-or-pid>");
        }

        var selector = NormalizeInputArgument(args[0]);
        var targets = audio.ResolveTargets(selector).ToList();
        if (targets.Count == 0)
        {
            return Fail($"No process or audio session matched '{selector}'.");
        }

        foreach (var target in targets.DistinctBy(static target => target.ProcessId))
        {
            TryClearRenderDevice(audio, target.ProcessId, target.ProcessName);
            Console.WriteLine($"Cleared persisted route for PID {target.ProcessId} ({target.ProcessName})");
        }

        return 0;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine();
        PrintUsage();
        return 1;
    }

    private static void TrySetRenderDevice(AudioEnvironment audio, int processId, string processName, string deviceId)
    {
        try
        {
            audio.Policy.SetRenderDeviceForProcess(processId, deviceId);
        }
        catch (Exception ex) when ((uint)ex.HResult == 0x80070005)
        {
            throw new InvalidOperationException(
                $"Windows denied writing the persisted audio route for PID {processId} ({processName}). " +
                "On this machine the hidden policy API is readable from this unpackaged CLI, but writes return E_ACCESSDENIED. " +
                "A packaged full-trust host similar to EarTrumpet may be required.",
                ex);
        }
    }

    private static void TryClearRenderDevice(AudioEnvironment audio, int processId, string processName)
    {
        try
        {
            audio.Policy.ClearRenderDeviceForProcess(processId);
        }
        catch (ArgumentException)
        {
            // The hidden policy API may report "Value does not fall within the expected range"
            // when a matching helper process has no persisted route to clear.
        }
        catch (Exception ex) when ((uint)ex.HResult == 0x80070005)
        {
            throw new InvalidOperationException(
                $"Windows denied clearing the persisted audio route for PID {processId} ({processName}). " +
                "On this machine the hidden policy API is readable from this unpackaged CLI, but writes return E_ACCESSDENIED. " +
                "A packaged full-trust host similar to EarTrumpet may be required.",
                ex);
        }
    }

    private static bool IsHelp(string arg) =>
        arg.Equals("/?", StringComparison.OrdinalIgnoreCase) ||
        arg.Equals("-h", StringComparison.OrdinalIgnoreCase) ||
        arg.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
        arg.Equals("help", StringComparison.OrdinalIgnoreCase);

    internal static string NormalizeInputArgument(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim();
        normalized = ExpandEnvironmentTokens(normalized);

        if (normalized.Length >= 2)
        {
            var first = normalized[0];
            var last = normalized[^1];
            if ((first == '"' && last == '"') || (first == '\'' && last == '\''))
            {
                normalized = normalized[1..^1].Trim();
            }
        }

        return normalized;
    }

    private static string ExpandEnvironmentTokens(string value)
    {
        var expanded = Environment.ExpandEnvironmentVariables(value);
        expanded = Regex.Replace(
            expanded,
            @"\$(?:env:)?([A-Za-z_][A-Za-z0-9_]*)",
            static match =>
            {
                var envValue = Environment.GetEnvironmentVariable(match.Groups[1].Value);
                return string.IsNullOrEmpty(envValue) ? match.Value : envValue;
            },
            RegexOptions.IgnoreCase);

        return expanded;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("AppAudioDeviceSwitcher");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  list-devices");
        Console.WriteLine("      List active render devices and their MMDevice IDs.");
        Console.WriteLine();
        Console.WriteLine("  list-apps [--all]");
        Console.WriteLine("      List current render audio sessions.");
        Console.WriteLine("      Default: only active sessions. Use --all to include inactive sessions.");
        Console.WriteLine();
        Console.WriteLine("  resolve <app-or-pid>");
        Console.WriteLine("      Show how a selector is normalized and which PID(s) it resolves to.");
        Console.WriteLine();
        Console.WriteLine("  set <app-or-pid> <device-id>");
        Console.WriteLine("      Persist an output device for a process name, PID, or wid:<window-id>.");
        Console.WriteLine();
        Console.WriteLine("  get <app-or-pid>");
        Console.WriteLine("      Show the persisted output device for a process name, PID, or wid:<window-id>.");
        Console.WriteLine();
        Console.WriteLine("  clear <app-or-pid>");
        Console.WriteLine("      Remove the persisted output device override for a process name, PID, or wid:<window-id>.");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  AppAudioDeviceSwitcher list-devices");
        Console.WriteLine("  AppAudioDeviceSwitcher list-apps");
        Console.WriteLine("  AppAudioDeviceSwitcher resolve %WG_TARGET_PID%");
        Console.WriteLine("  AppAudioDeviceSwitcher set chrome {0.0.0.00000000}.{GUID}");
        Console.WriteLine("  AppAudioDeviceSwitcher set 12345 {0.0.0.00000000}.{GUID}");
        Console.WriteLine("  AppAudioDeviceSwitcher set wid:197694 {0.0.0.00000000}.{GUID}");
        Console.WriteLine("  AppAudioDeviceSwitcher get chrome");
        Console.WriteLine("  AppAudioDeviceSwitcher clear chrome");
    }
}

internal sealed class AudioEnvironment : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();

    public AudioRoutingPolicy Policy { get; } = new();

    public IReadOnlyList<MMDevice> GetRenderDevices()
    {
        var collection = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        var devices = new List<MMDevice>(collection.Count);
        for (var i = 0; i < collection.Count; i++)
        {
            devices.Add(collection[i]);
        }

        return devices;
    }

    public MMDevice? TryGetDefaultRenderDevice()
    {
        try
        {
            return _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        catch
        {
            return null;
        }
    }

    public bool DeviceExists(string deviceId) => TryGetRenderDevice(deviceId) != null;

    public MMDevice? TryGetRenderDevice(string deviceId)
    {
        try
        {
            return _enumerator.GetDevice(deviceId);
        }
        catch
        {
            return null;
        }
    }

    public IEnumerable<RenderSessionInfo> GetRenderSessions(bool includeInactive, bool includeExecutablePath = true)
    {
        var processPathCache = new Dictionary<int, string?>();
        var processNameCache = new Dictionary<int, string?>();
        var persistedRouteCache = new Dictionary<int, string?>();

        foreach (var device in GetRenderDevices())
        {
            SessionCollection? sessions;
            try
            {
                sessions = device.AudioSessionManager.Sessions;
            }
            catch
            {
                continue;
            }

            for (var i = 0; i < sessions.Count; i++)
            {
                using var session = sessions[i];
                var state = session.State;
                if (!includeInactive && state != AudioSessionState.AudioSessionStateActive)
                {
                    continue;
                }

                var processId = unchecked((int)session.GetProcessID);
                if (!processPathCache.TryGetValue(processId, out var executablePath))
                {
                    executablePath = includeExecutablePath ? TryGetProcessPath(processId) : null;
                    processPathCache[processId] = executablePath;
                }

                if (!processNameCache.TryGetValue(processId, out var processName))
                {
                    processName = GetDisplayProcessName(processId, executablePath, session);
                    processNameCache[processId] = processName;
                }

                if (!persistedRouteCache.TryGetValue(processId, out var persistedRoute))
                {
                    persistedRoute = Policy.GetRenderDeviceForProcess(processId);
                    persistedRouteCache[processId] = persistedRoute;
                }

                yield return new RenderSessionInfo(
                    processId,
                    processName ?? $"PID-{processId}",
                    executablePath,
                    device.ID,
                    device.FriendlyName,
                    state.ToString(),
                    persistedRoute);
            }
        }
    }

    public IEnumerable<ProcessTarget> ResolveTargets(string selector)
    {
        var resolution = ResolveSelector(selector);
        foreach (var target in resolution.Targets)
        {
            yield return target;
        }
    }

    public SelectorResolution ResolveSelector(string selector)
    {
        selector = ProgramEntry.NormalizeInputArgument(selector);

        if (TryResolveSelectorToProcessId(selector, out var pid, out var resolutionKind, out var windowHandle))
        {
            var processName = TryGetProcessName(pid) ?? $"PID-{pid}";
            return new SelectorResolution(
                selector,
                resolutionKind,
                windowHandle,
                pid,
                new[] { new ProcessTarget(pid, processName) });
        }

        var matchOptions = CreateSelectorMatchOptions(selector);
        return new SelectorResolution(
            matchOptions.NormalizedSelector,
            matchOptions.RequiresExecutablePath ? "name-or-path" : "process-name",
            IntPtr.Zero,
            null,
            ResolveTargetsByName(matchOptions).ToArray());
    }

    public void Dispose()
    {
        _enumerator.Dispose();
        Policy.Dispose();
    }

    private IEnumerable<ProcessTarget> ResolveTargetsByName(SelectorMatchOptions matchOptions)
    {
        var yielded = new HashSet<int>();
        var sessionTargets = GetMatchingSessionTargets(matchOptions).ToList();

        foreach (var sessionTarget in sessionTargets)
        {
            if (yielded.Add(sessionTarget.ProcessId))
            {
                yield return sessionTarget;
            }
        }

        if (yielded.Count > 0)
        {
            yield break;
        }

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (NormalizeSelector(process.ProcessName) == matchOptions.NormalizedSelector)
                {
                    if (yielded.Add(process.Id))
                    {
                        yield return new ProcessTarget(process.Id, process.ProcessName);
                    }

                    continue;
                }

                if (!matchOptions.RequiresExecutablePath)
                {
                    continue;
                }

                var executablePath = TryGetProcessPath(process.Id);
                if (!MatchesSelector(process.ProcessName, executablePath, matchOptions) || !yielded.Add(process.Id))
                {
                    continue;
                }

                yield return new ProcessTarget(process.Id, process.ProcessName);
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private IEnumerable<ProcessTarget> GetMatchingSessionTargets(SelectorMatchOptions matchOptions)
    {
        var processPathCache = new Dictionary<int, string?>();
        var processNameCache = new Dictionary<int, string?>();

        foreach (var device in GetRenderDevices())
        {
            SessionCollection? sessions;
            try
            {
                sessions = device.AudioSessionManager.Sessions;
            }
            catch
            {
                continue;
            }

            for (var i = 0; i < sessions.Count; i++)
            {
                using var session = sessions[i];
                var processId = unchecked((int)session.GetProcessID);

                if (!processPathCache.TryGetValue(processId, out var executablePath))
                {
                    executablePath = matchOptions.RequiresExecutablePath ? TryGetProcessPath(processId) : null;
                    processPathCache[processId] = executablePath;
                }

                if (!processNameCache.TryGetValue(processId, out var processName))
                {
                    processName = GetDisplayProcessName(processId, executablePath, session);
                    processNameCache[processId] = processName;
                }

                if (MatchesSelector(processName ?? $"PID-{processId}", executablePath, matchOptions))
                {
                    yield return new ProcessTarget(processId, processName ?? $"PID-{processId}");
                }
            }
        }
    }

    private static bool MatchesSelector(string processName, string? executablePath, SelectorMatchOptions matchOptions)
    {
        if (NormalizeSelector(processName) == matchOptions.NormalizedSelector)
        {
            return true;
        }

        if (!matchOptions.RequiresExecutablePath || string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        if (NormalizeSelector(executablePath) == matchOptions.NormalizedSelector)
        {
            return true;
        }

        var fileName = Path.GetFileName(executablePath);
        return NormalizeSelector(fileName) == matchOptions.NormalizedSelector;
    }

    private static string NormalizeSelector(string? value)
    {
        value = ProgramEntry.NormalizeInputArgument(value);
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim().ToLowerInvariant();
        return normalized.EndsWith(".exe", StringComparison.Ordinal) ? normalized[..^4] : normalized;
    }

    private static SelectorMatchOptions CreateSelectorMatchOptions(string selector)
    {
        var normalizedSelector = NormalizeSelector(selector);
        var requiresExecutablePath =
            normalizedSelector.Contains('\\') ||
            normalizedSelector.Contains('/');

        return new SelectorMatchOptions(normalizedSelector, requiresExecutablePath);
    }

    private static bool TryResolveSelectorToProcessId(
        string selector,
        out int processId,
        out string resolutionKind,
        out IntPtr windowHandle)
    {
        processId = 0;
        resolutionKind = "unresolved";
        windowHandle = IntPtr.Zero;
        if (string.IsNullOrWhiteSpace(selector))
        {
            return false;
        }

        if (TryParseWindowSelector(selector, out windowHandle))
        {
            if (TryGetProcessIdFromWindow(windowHandle, out processId))
            {
                resolutionKind = "window-id";
                return true;
            }

            return false;
        }

        if (int.TryParse(selector, out processId) && ProcessExists(processId))
        {
            resolutionKind = "pid";
            return true;
        }

        if (long.TryParse(selector, out var rawHandle) &&
            rawHandle > 0 &&
            rawHandle <= long.MaxValue &&
            TryGetProcessIdFromWindow(new IntPtr(rawHandle), out processId))
        {
            windowHandle = new IntPtr(rawHandle);
            resolutionKind = "window-id(auto)";
            return true;
        }

        if (selector.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            long.TryParse(selector[2..], System.Globalization.NumberStyles.HexNumber, null, out var hexHandle) &&
            TryGetProcessIdFromWindow(new IntPtr(hexHandle), out processId))
        {
            windowHandle = new IntPtr(hexHandle);
            resolutionKind = "window-id(auto)";
            return true;
        }

        processId = 0;
        return false;
    }

    private static bool TryParseWindowSelector(string selector, out IntPtr windowHandle)
    {
        windowHandle = IntPtr.Zero;
        var prefixes = new[] { "wid:", "hwnd:", "window:" };
        foreach (var prefix in prefixes)
        {
            if (!selector.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = selector[prefix.Length..].Trim();
            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                long.TryParse(value[2..], System.Globalization.NumberStyles.HexNumber, null, out var hexHandle))
            {
                windowHandle = new IntPtr(hexHandle);
                return true;
            }

            if (long.TryParse(value, out var decimalHandle))
            {
                windowHandle = new IntPtr(decimalHandle);
                return true;
            }

            return false;
        }

        return false;
    }

    private static bool TryGetProcessIdFromWindow(IntPtr windowHandle, out int processId)
    {
        processId = 0;
        if (windowHandle == IntPtr.Zero)
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(windowHandle, out var pid);
        if (pid == 0)
        {
            return false;
        }

        processId = unchecked((int)pid);
        return true;
    }

    private static bool ProcessExists(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            _ = process.Id;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string GetDisplayProcessName(int processId, string? executablePath, AudioSessionControl session)
    {
        if (processId == 0 || session.IsSystemSoundsSession)
        {
            return "System Sounds";
        }

        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            return Path.GetFileNameWithoutExtension(executablePath);
        }

        return TryGetProcessName(processId) ?? $"PID-{processId}";
    }

    private static string? TryGetProcessName(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetProcessPath(int processId)
    {
        if (processId <= 0)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }
}

internal sealed class AudioRoutingPolicy : IDisposable
{
    private const string ActivatableClassId = "Windows.Media.Internal.AudioPolicyConfig";
    private const string MmDeviceApiToken = @"\\?\SWD#MMDEVAPI#";
    private const string RenderInterfaceSuffix = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
    private static readonly Guid PolicyConfig21H2Iid = new("ab3d4648-e242-459f-b02f-541c70306324");
    private static readonly Guid PolicyConfigDownlevelIid = new("2a59116d-6c4f-45e0-a74f-707e3fef9258");
    private const int SetPersistedDefaultAudioEndpointSlot = 25;
    private const int GetPersistedDefaultAudioEndpointSlot = 26;

    private IntPtr _factoryPtr;
    private SetPersistedDefaultAudioEndpointDelegate? _setEndpoint;
    private GetPersistedDefaultAudioEndpointDelegate? _getEndpoint;

    public void SetRenderDeviceForProcess(int processId, string deviceId)
    {
        SetRenderDeviceCore(processId, deviceId);
    }

    public void ClearRenderDeviceForProcess(int processId)
    {
        SetRenderDeviceCore(processId, null);
    }

    public string? GetRenderDeviceForProcess(int processId)
    {
        EnsureFactory();
        var hr = GetEndpoint(_factoryPtr, (uint)processId, PolicyDataFlow.Render, PolicyRole.MultimediaOrConsole, out var packedDeviceIdHandle);
        if (hr == HResult.ERROR_NOT_FOUND || packedDeviceIdHandle == IntPtr.Zero)
        {
            return null;
        }

        Marshal.ThrowExceptionForHR((int)hr);

        try
        {
            var packedDeviceId = WinRtInterop.HStringToString(packedDeviceIdHandle);
            return string.IsNullOrWhiteSpace(packedDeviceId) ? null : UnpackDeviceId(packedDeviceId);
        }
        finally
        {
            WinRtInterop.WindowsDeleteString(packedDeviceIdHandle);
        }
    }

    public void Dispose()
    {
        if (_factoryPtr != IntPtr.Zero)
        {
            Marshal.Release(_factoryPtr);
            _factoryPtr = IntPtr.Zero;
        }
    }

    private void SetRenderDeviceCore(int processId, string? deviceId)
    {
        EnsureFactory();
        IntPtr hstring = IntPtr.Zero;
        try
        {
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                var packed = PackDeviceId(deviceId);
                WinRtInterop.WindowsCreateString(packed, (uint)packed.Length, out hstring);
            }

            var hr1 = SetEndpoint(_factoryPtr, (uint)processId, PolicyDataFlow.Render, PolicyRole.Multimedia, hstring);
            Marshal.ThrowExceptionForHR((int)hr1);

            var hr2 = SetEndpoint(_factoryPtr, (uint)processId, PolicyDataFlow.Render, PolicyRole.Console, hstring);
            Marshal.ThrowExceptionForHR((int)hr2);
        }
        finally
        {
            if (hstring != IntPtr.Zero)
            {
                WinRtInterop.WindowsDeleteString(hstring);
            }
        }
    }

    private void EnsureFactory()
    {
        if (_factoryPtr != IntPtr.Zero)
        {
            return;
        }

        var initHr = WinRtInterop.RoInitialize(0);
        if (initHr < 0 && initHr != unchecked((int)0x80010106))
        {
            Marshal.ThrowExceptionForHR(initHr);
        }

        _factoryPtr = TryCreateFactory(PolicyConfig21H2Iid);
        if (_factoryPtr == IntPtr.Zero)
        {
            _factoryPtr = TryCreateFactory(PolicyConfigDownlevelIid);
        }

        if (_factoryPtr == IntPtr.Zero)
        {
            throw new InvalidOperationException("Unable to activate Windows.Media.Internal.AudioPolicyConfig.");
        }

        _setEndpoint = GetDelegate<SetPersistedDefaultAudioEndpointDelegate>(_factoryPtr, SetPersistedDefaultAudioEndpointSlot);
        _getEndpoint = GetDelegate<GetPersistedDefaultAudioEndpointDelegate>(_factoryPtr, GetPersistedDefaultAudioEndpointSlot);
    }

    private static IntPtr TryCreateFactory(Guid iid)
    {
        IntPtr classId = IntPtr.Zero;
        try
        {
            WinRtInterop.WindowsCreateString(ActivatableClassId, (uint)ActivatableClassId.Length, out classId);
            var hr = WinRtInterop.RoGetActivationFactory(classId, ref iid, out var factoryPtr);
            return hr >= 0 ? factoryPtr : IntPtr.Zero;
        }
        catch
        {
            return IntPtr.Zero;
        }
        finally
        {
            if (classId != IntPtr.Zero)
            {
                WinRtInterop.WindowsDeleteString(classId);
            }
        }
    }

    private SetPersistedDefaultAudioEndpointDelegate SetEndpoint => _setEndpoint ?? throw new InvalidOperationException("Policy config set delegate was not initialized.");

    private GetPersistedDefaultAudioEndpointDelegate GetEndpoint => _getEndpoint ?? throw new InvalidOperationException("Policy config get delegate was not initialized.");

    private static TDelegate GetDelegate<TDelegate>(IntPtr interfacePtr, int slot)
        where TDelegate : Delegate
    {
        var vtable = Marshal.ReadIntPtr(interfacePtr);
        var methodPtr = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<TDelegate>(methodPtr);
    }

    private static string PackDeviceId(string deviceId) => $"{MmDeviceApiToken}{deviceId}{RenderInterfaceSuffix}";

    private static string UnpackDeviceId(string packedDeviceId)
    {
        var unpacked = packedDeviceId;
        if (unpacked.StartsWith(MmDeviceApiToken, StringComparison.OrdinalIgnoreCase))
        {
            unpacked = unpacked[MmDeviceApiToken.Length..];
        }

        if (unpacked.EndsWith(RenderInterfaceSuffix, StringComparison.OrdinalIgnoreCase))
        {
            unpacked = unpacked[..^RenderInterfaceSuffix.Length];
        }

        return unpacked;
    }
}

internal readonly record struct RenderSessionInfo(
    int ProcessId,
    string ProcessName,
    string? ExecutablePath,
    string DeviceId,
    string DeviceName,
    string State,
    string? PersistedDeviceId);

internal readonly record struct ProcessTarget(int ProcessId, string ProcessName);

internal readonly record struct SelectorMatchOptions(string NormalizedSelector, bool RequiresExecutablePath);

internal readonly record struct SelectorResolution(
    string NormalizedSelector,
    string ResolutionKind,
    IntPtr WindowHandle,
    int? ResolvedProcessId,
    IReadOnlyList<ProcessTarget> Targets);

internal static class NativeMethods
{
    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}

internal static class WinRtInterop
{
    [DllImport("combase.dll")]
    internal static extern int RoInitialize(uint initType);

    [DllImport("combase.dll")]
    internal static extern int RoGetActivationFactory(
        IntPtr activatableClassId,
        ref Guid iid,
        out IntPtr factory);

    [DllImport("combase.dll", PreserveSig = false)]
    internal static extern void WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string source,
        uint length,
        out IntPtr hstring);

    [DllImport("combase.dll")]
    internal static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern IntPtr WindowsGetStringRawBuffer(IntPtr hstring, out uint length);

    internal static string HStringToString(IntPtr hstring)
    {
        var buffer = WindowsGetStringRawBuffer(hstring, out var length);
        return buffer == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUni(buffer, (int)length) ?? string.Empty;
    }
}

internal enum HResult : uint
{
    ERROR_NOT_FOUND = 0x80070490
}

internal enum PolicyDataFlow
{
    Render = 0,
    Capture = 1,
    All = 2
}

[Flags]
internal enum PolicyRole
{
    Console = 0,
    Multimedia = 1,
    Communications = 2,
    MultimediaOrConsole = Console | Multimedia
}

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate HResult SetPersistedDefaultAudioEndpointDelegate(
    IntPtr thisPtr,
    uint processId,
    PolicyDataFlow flow,
    PolicyRole role,
    IntPtr deviceId);

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate HResult GetPersistedDefaultAudioEndpointDelegate(
    IntPtr thisPtr,
    uint processId,
    PolicyDataFlow flow,
    PolicyRole role,
    out IntPtr deviceId);
