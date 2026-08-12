// Derived from zhy0504/Codex-PowerShell7-OneClick-Fix.
// This distributable variant resolves its fallback shell at runtime.
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;

[assembly: AssemblyTitle("Codex Windows Internal Command Helper")]
[assembly: AssemblyDescription("Handles known Codex Windows operations without starting PowerShell")]
[assembly: AssemblyCompany("Community patch")]
[assembly: AssemblyProduct("Codex Windows Desktop Patch")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

internal static class PowerShellShim
{
    private const string ShimVersion = "1.0.0";
    private const string PreferredPowerShellVariable = "CODEX_PWSH_PATH";
    private const string LauncherDirectoryName = "codex-pwsh";
    private const string WindowsPowerShellLauncherFilename = "powershell.exe";
    private const string PowerShell7LauncherFilename = "pwsh.exe";
    private const string DisableOptimizationsVariable = "CODEX_PWSH_SHIM_DISABLE_OPTIMIZATIONS";
    private const string RequireOptimizationsVariable = "CODEX_PWSH_SHIM_REQUIRE_OPTIMIZATIONS";
    private const string SelfTestArgument = "--codex-pwsh-shim-self-test";
    private const string DesktopMetadataArgument = "--codex-desktop-metadata-v1";
    private const string TreeCommand = "$ErrorActionPreference = 'Stop'; Get-CimInstance Win32_Process | Select-Object ProcessId,ParentProcessId | ConvertTo-Json -Depth 2";
    private const string ExecutablePathCommand = "$ErrorActionPreference = 'Stop'; Get-CimInstance Win32_Process | Select-Object ProcessId,ExecutablePath,CommandLine | ConvertTo-Json -Depth 2";
    private const string PerformancePrefix = "$ErrorActionPreference = 'Stop'; $cpuByPid = @{}; Get-CimInstance Win32_PerfFormattedData_PerfProc_Process | ForEach-Object { $cpuByPid[[int]$_.IDProcess] = [double]$_.PercentProcessorTime }; ";
    private const string ProcessQuery = "Get-CimInstance Win32_Process";
    private const string ProcessFilterPrefix = "Get-CimInstance Win32_Process -Filter \"";
    private const string PerformanceSuffix = " | Select-Object ProcessId,ParentProcessId,CommandLine,WorkingSetSize,@{Name='CpuPercent';Expression={$cpuByPid[[int]$_.ProcessId]}},@{Name='AgeSeconds';Expression={[int]((Get-Date) - $_.CreationDate).TotalSeconds}} | ConvertTo-Json -Depth 2";
    private const string ZipListCommand = "param($ArchivePath)\n$ErrorActionPreference = 'Stop'\nAdd-Type -AssemblyName System.IO.Compression.FileSystem\n$archive = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)\ntry { $archive.Entries | ForEach-Object { $_.FullName } } finally { $archive.Dispose() }";
    private const string ZipExtractCommand = "param($ArchivePath, $ExtractDir)\n$ErrorActionPreference = 'Stop'\nExpand-Archive -LiteralPath $ArchivePath -DestinationPath $ExtractDir -Force";
    private const uint SnapshotProcesses = 0x00000002;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int ErrorNoMoreFiles = 18;

    private static readonly Regex ProcessFilterPattern = new Regex(
        @"\AProcessId = (?<pid>\d+)(?: OR ProcessId = (?<pid>\d+))*\z",
        RegexOptions.CultureInvariant);
    private static readonly Regex ExecutableNamePattern = new Regex(
        @"[^\\/!.]+\.exe",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly StringComparer UserCultureComparer = CreateUserCultureComparer();
    private static readonly IComparer<string> ProcessKeyComparer = new PowerShellProcessKeyComparer();
    private static int PowerShellLaunchCount;

    // PowerShell 7 uses ICU ordering; .NET Framework NLS otherwise puts Latin keys first.
    private sealed class PowerShellProcessKeyComparer : IComparer<string>
    {
        public int Compare(string left, string right)
        {
            string normalizedLeft = left ?? String.Empty;
            string normalizedRight = right ?? String.Empty;
            int sharedLength = Math.Min(normalizedLeft.Length, normalizedRight.Length);
            for (int index = 0; index < sharedLength; index++)
            {
                char leftCharacter = Char.ToLowerInvariant(normalizedLeft[index]);
                char rightCharacter = Char.ToLowerInvariant(normalizedRight[index]);
                if (leftCharacter == rightCharacter)
                {
                    continue;
                }
                bool leftAscii = leftCharacter <= 0x7f;
                bool rightAscii = rightCharacter <= 0x7f;
                if (leftAscii != rightAscii)
                {
                    return leftAscii ? 1 : -1;
                }
                if (leftAscii)
                {
                    return leftCharacter.CompareTo(rightCharacter);
                }
                break;
            }
            return UserCultureComparer.Compare(normalizedLeft, normalizedRight);
        }
    }

    private enum JobObjectInfoType
    {
        ExtendedLimitInformation = 9
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExecutableFile;
    }

    private sealed class ProcessDetails
    {
        public uint ProcessId;
        public uint ParentProcessId;
        public string CommandLine;
        public ulong? WorkingSetSize;
        public double? CpuPercent;
        public int? AgeSeconds;
    }

    private sealed class ExecutableProcessDetails
    {
        public uint ProcessId;
        public string ExecutablePath;
        public string CommandLine;
    }

    private sealed class UserAssistEntry
    {
        public string Name;
        public int RunCount;
        public long LastRunMilliseconds;
    }

    private sealed class StartApp
    {
        public string Name;
        public string AppId;
    }

    private sealed class RunningProcess
    {
        public string Name;
        public string Key;
        public string Path;
    }

    private sealed class DesktopApp
    {
        public string BundleId;
        public string DisplayName;
        public string AppPath;
        public bool IsRunning;
        public long LastUsedDateRanking;
        public List<string> ProcessKeys;
        public int UseCount;
    }

    private sealed class UserAssistMatch
    {
        public UserAssistEntry Entry;
        public int Rank;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr jobAttributes, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr job,
        JobObjectInfoType infoType,
        ref JobObjectExtendedLimitInformation info,
        uint infoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [STAThread]
    private static int Main()
    {
        if (IsTransparentLauncher())
        {
            return ForwardToPowerShell(true);
        }

        string[] arguments = Environment.GetCommandLineArgs();
        if (arguments.Length == 2 && String.Equals(arguments[1], SelfTestArgument, StringComparison.Ordinal))
        {
            return RunSelfTest();
        }

        if (arguments.Length == 3 && String.Equals(arguments[1], DesktopMetadataArgument, StringComparison.Ordinal))
        {
            return RunDesktopMetadata(arguments[2]);
        }

        if (!IsEnvironmentFlagEnabled(DisableOptimizationsVariable))
        {
            string command;
            List<string> commandArguments;
            if (TryGetCommandInvocation(arguments, out command, out commandArguments))
            {
                try
                {
                    string json;
                    int rowCount;
                    if (commandArguments.Count == 0 && String.Equals(command, TreeCommand, StringComparison.Ordinal))
                    {
                        json = BuildNativeProcessTreeJson(out rowCount);
                        WriteStandardOutput(json);
                        return 0;
                    }

                    List<uint> processIds;
                    if (commandArguments.Count == 0 && TryParsePerformanceCommand(command, out processIds))
                    {
                        json = BuildProcessDetailsJson(processIds, out rowCount);
                        WriteStandardOutput(json);
                        return 0;
                    }

                    if (commandArguments.Count == 0 && String.Equals(command, ExecutablePathCommand, StringComparison.Ordinal))
                    {
                        json = BuildExecutableProcessJson(out rowCount);
                        WriteStandardOutput(json);
                        return 0;
                    }

                    if (commandArguments.Count == 1 && String.Equals(command, ZipListCommand, StringComparison.Ordinal))
                    {
                        WriteStandardOutput(BuildZipListing(commandArguments[0], out rowCount));
                        return 0;
                    }

                    if (commandArguments.Count == 2 && String.Equals(command, ZipExtractCommand, StringComparison.Ordinal))
                    {
                        ExtractZipArchive(commandArguments[0], commandArguments[1], out rowCount);
                        return 0;
                    }
                }
                catch (Exception error)
                {
                    if (IsEnvironmentFlagEnabled(RequireOptimizationsVariable))
                    {
                        Console.Error.WriteLine("Codex Windows internal helper optimization failed: " + error.Message);
                        return 9010;
                    }
                }
            }
        }

        return ForwardToPowerShell(false);
    }

    private static int RunSelfTest()
    {
        try
        {
            int nativeRows;
            int detailRows;
            int executableRows;
            int desktopRows;
            int zipRows;
            Stopwatch timer = Stopwatch.StartNew();
            BuildNativeProcessTreeJson(out nativeRows);
            long processTreeMilliseconds = timer.ElapsedMilliseconds;
            timer.Restart();
            BuildProcessDetailsJson(new List<uint> { (uint)Process.GetCurrentProcess().Id }, out detailRows);
            long processDetailsMilliseconds = timer.ElapsedMilliseconds;
            timer.Restart();
            BuildExecutableProcessJson(out executableRows);
            long executablePathMilliseconds = timer.ElapsedMilliseconds;
            timer.Restart();
            BuildDesktopMetadataJson(out desktopRows);
            long desktopMetadataMilliseconds = timer.ElapsedMilliseconds;
            timer.Restart();
            RunZipSelfTest(out zipRows);
            long zipRoundTripMilliseconds = timer.ElapsedMilliseconds;
            if (nativeRows < 1 || detailRows != 1 || executableRows < 1 || desktopRows < 1 ||
                zipRows != 1 || PowerShellLaunchCount != 0)
            {
                throw new InvalidOperationException("An optimized query returned an unexpected row count.");
            }

            string json = "{\"ShimVersion\":\"" + ShimVersion +
                "\",\"NativeProcessRows\":" + nativeRows.ToString(CultureInfo.InvariantCulture) +
                ",\"DirectWmiRows\":" + detailRows.ToString(CultureInfo.InvariantCulture) +
                ",\"ExecutablePathRows\":" + executableRows.ToString(CultureInfo.InvariantCulture) +
                ",\"DesktopAppRows\":" + desktopRows.ToString(CultureInfo.InvariantCulture) +
                ",\"ZipRoundTripFiles\":" + zipRows.ToString(CultureInfo.InvariantCulture) +
                ",\"PowerShellChildProcesses\":" + PowerShellLaunchCount.ToString(CultureInfo.InvariantCulture) +
                ",\"DurationsMs\":{\"ProcessTree\":" + processTreeMilliseconds.ToString(CultureInfo.InvariantCulture) +
                ",\"ProcessDetails\":" + processDetailsMilliseconds.ToString(CultureInfo.InvariantCulture) +
                ",\"ExecutablePath\":" + executablePathMilliseconds.ToString(CultureInfo.InvariantCulture) +
                ",\"DesktopMetadata\":" + desktopMetadataMilliseconds.ToString(CultureInfo.InvariantCulture) +
                ",\"ZipRoundTrip\":" + zipRoundTripMilliseconds.ToString(CultureInfo.InvariantCulture) + "}}";
            WriteStandardOutput(json);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("Codex PowerShell query helper self-test failed: " + error.Message);
            return 9010;
        }
    }

    private static bool TryGetCommandInvocation(
        string[] arguments,
        out string command,
        out List<string> commandArguments)
    {
        command = null;
        commandArguments = new List<string>();
        bool hasNoProfile = false;
        bool hasNonInteractive = false;

        for (int index = 1; index < arguments.Length; index++)
        {
            if (String.Equals(arguments[index], "-NoProfile", StringComparison.OrdinalIgnoreCase))
            {
                hasNoProfile = true;
            }
            else if (String.Equals(arguments[index], "-NonInteractive", StringComparison.OrdinalIgnoreCase))
            {
                hasNonInteractive = true;
            }
            else if (String.Equals(arguments[index], "-Command", StringComparison.OrdinalIgnoreCase) && index < arguments.Length - 1)
            {
                command = arguments[index + 1];
                for (int argumentIndex = index + 2; argumentIndex < arguments.Length; argumentIndex++)
                {
                    commandArguments.Add(arguments[argumentIndex]);
                }
                break;
            }
        }

        return hasNoProfile && hasNonInteractive && command != null;
    }

    private static bool TryParsePerformanceCommand(string command, out List<uint> processIds)
    {
        processIds = null;
        if (!command.StartsWith(PerformancePrefix, StringComparison.Ordinal) ||
            !command.EndsWith(PerformanceSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        string processPart = command.Substring(
            PerformancePrefix.Length,
            command.Length - PerformancePrefix.Length - PerformanceSuffix.Length);

        if (String.Equals(processPart, ProcessQuery, StringComparison.Ordinal))
        {
            return true;
        }

        if (!processPart.StartsWith(ProcessFilterPrefix, StringComparison.Ordinal) ||
            processPart.Length <= ProcessFilterPrefix.Length ||
            processPart[processPart.Length - 1] != '"')
        {
            return false;
        }

        string filter = processPart.Substring(
            ProcessFilterPrefix.Length,
            processPart.Length - ProcessFilterPrefix.Length - 1);
        Match match = ProcessFilterPattern.Match(filter);
        if (!match.Success)
        {
            return false;
        }

        processIds = new List<uint>();
        HashSet<uint> seen = new HashSet<uint>();
        foreach (Capture capture in match.Groups["pid"].Captures)
        {
            uint processId;
            if (!UInt32.TryParse(capture.Value, NumberStyles.None, CultureInfo.InvariantCulture, out processId))
            {
                return false;
            }
            if (seen.Add(processId))
            {
                processIds.Add(processId);
            }
        }

        return processIds.Count > 0;
    }

    private static string BuildNativeProcessTreeJson(out int rowCount)
    {
        IntPtr snapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0);
        if (snapshot == new IntPtr(-1))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            StringBuilder json = new StringBuilder(8192);
            json.Append('[');
            rowCount = 0;
            ProcessEntry32 entry = new ProcessEntry32();
            entry.Size = (uint)Marshal.SizeOf(typeof(ProcessEntry32));

            if (!Process32First(snapshot, ref entry))
            {
                int error = Marshal.GetLastWin32Error();
                if (error != ErrorNoMoreFiles)
                {
                    throw new Win32Exception(error);
                }
            }
            else
            {
                while (true)
                {
                    if (rowCount > 0)
                    {
                        json.Append(',');
                    }
                    json.Append("{\"ProcessId\":");
                    json.Append(entry.ProcessId.ToString(CultureInfo.InvariantCulture));
                    json.Append(",\"ParentProcessId\":");
                    json.Append(entry.ParentProcessId.ToString(CultureInfo.InvariantCulture));
                    json.Append('}');
                    rowCount++;

                    entry.Size = (uint)Marshal.SizeOf(typeof(ProcessEntry32));
                    if (!Process32Next(snapshot, ref entry))
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error != ErrorNoMoreFiles)
                        {
                            throw new Win32Exception(error);
                        }
                        break;
                    }
                }
            }

            json.Append(']');
            if (rowCount < 1)
            {
                throw new InvalidOperationException("The native process snapshot was empty.");
            }
            return json.ToString();
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    private static string BuildProcessDetailsJson(List<uint> requestedProcessIds, out int rowCount)
    {
        ManagementScope scope = new ManagementScope(@"\\.\root\cimv2");
        scope.Options.Timeout = TimeSpan.FromSeconds(4);
        scope.Connect();

        string performanceQuery = "SELECT IDProcess,PercentProcessorTime FROM Win32_PerfFormattedData_PerfProc_Process";
        string processQuery = "SELECT ProcessId,ParentProcessId,CommandLine,WorkingSetSize,CreationDate FROM Win32_Process";
        if (requestedProcessIds != null)
        {
            performanceQuery += " WHERE " + BuildWqlFilter("IDProcess", requestedProcessIds);
            processQuery += " WHERE " + BuildWqlFilter("ProcessId", requestedProcessIds);
        }

        Dictionary<uint, double> cpuByProcessId = new Dictionary<uint, double>();
        using (ManagementObjectSearcher searcher = CreateSearcher(scope, performanceQuery))
        using (ManagementObjectCollection rows = searcher.Get())
        {
            foreach (ManagementObject row in rows)
            {
                using (row)
                {
                    uint processId = Convert.ToUInt32(row["IDProcess"], CultureInfo.InvariantCulture);
                    double cpuPercent = Convert.ToDouble(row["PercentProcessorTime"], CultureInfo.InvariantCulture);
                    cpuByProcessId[processId] = cpuPercent;
                }
            }
        }

        List<ProcessDetails> details = new List<ProcessDetails>();
        using (ManagementObjectSearcher searcher = CreateSearcher(scope, processQuery))
        using (ManagementObjectCollection rows = searcher.Get())
        {
            foreach (ManagementObject row in rows)
            {
                using (row)
                {
                    ProcessDetails item = new ProcessDetails();
                    item.ProcessId = Convert.ToUInt32(row["ProcessId"], CultureInfo.InvariantCulture);
                    item.ParentProcessId = Convert.ToUInt32(row["ParentProcessId"], CultureInfo.InvariantCulture);
                    item.CommandLine = row["CommandLine"] == null ? null : Convert.ToString(row["CommandLine"], CultureInfo.InvariantCulture);
                    item.WorkingSetSize = row["WorkingSetSize"] == null
                        ? (ulong?)null
                        : Convert.ToUInt64(row["WorkingSetSize"], CultureInfo.InvariantCulture);

                    double cpuPercent;
                    item.CpuPercent = cpuByProcessId.TryGetValue(item.ProcessId, out cpuPercent)
                        ? (double?)cpuPercent
                        : null;
                    item.AgeSeconds = GetAgeSeconds(row["CreationDate"]);
                    details.Add(item);
                }
            }
        }

        rowCount = details.Count;
        return SerializeProcessDetails(details);
    }

    private static string BuildExecutableProcessJson(out int rowCount)
    {
        ManagementScope scope = new ManagementScope(@"\\.\root\cimv2");
        scope.Options.Timeout = TimeSpan.FromSeconds(4);
        scope.Connect();

        List<ExecutableProcessDetails> details = new List<ExecutableProcessDetails>();
        using (ManagementObjectSearcher searcher = CreateSearcher(
            scope,
            "SELECT ProcessId,ExecutablePath,CommandLine FROM Win32_Process"))
        using (ManagementObjectCollection rows = searcher.Get())
        {
            foreach (ManagementObject row in rows)
            {
                using (row)
                {
                    ExecutableProcessDetails item = new ExecutableProcessDetails();
                    item.ProcessId = Convert.ToUInt32(row["ProcessId"], CultureInfo.InvariantCulture);
                    item.ExecutablePath = row["ExecutablePath"] == null
                        ? null
                        : Convert.ToString(row["ExecutablePath"], CultureInfo.InvariantCulture);
                    item.CommandLine = row["CommandLine"] == null
                        ? null
                        : Convert.ToString(row["CommandLine"], CultureInfo.InvariantCulture);
                    details.Add(item);
                }
            }
        }

        rowCount = details.Count;
        StringBuilder json = new StringBuilder(Math.Max(2, details.Count * 160));
        json.Append('[');
        for (int index = 0; index < details.Count; index++)
        {
            if (index > 0)
            {
                json.Append(',');
            }
            ExecutableProcessDetails item = details[index];
            json.Append("{\"ProcessId\":");
            json.Append(item.ProcessId.ToString(CultureInfo.InvariantCulture));
            json.Append(",\"ExecutablePath\":");
            AppendJsonString(json, item.ExecutablePath);
            json.Append(",\"CommandLine\":");
            AppendJsonString(json, item.CommandLine);
            json.Append('}');
        }
        json.Append(']');
        return json.ToString();
    }

    private static ManagementObjectSearcher CreateSearcher(ManagementScope scope, string query)
    {
        EnumerationOptions options = new EnumerationOptions();
        options.ReturnImmediately = true;
        options.Rewindable = false;
        options.Timeout = TimeSpan.FromSeconds(4);
        return new ManagementObjectSearcher(scope, new ObjectQuery(query), options);
    }

    private static string BuildWqlFilter(string propertyName, List<uint> processIds)
    {
        StringBuilder filter = new StringBuilder(processIds.Count * 24);
        for (int index = 0; index < processIds.Count; index++)
        {
            if (index > 0)
            {
                filter.Append(" OR ");
            }
            filter.Append(propertyName);
            filter.Append(" = ");
            filter.Append(processIds[index].ToString(CultureInfo.InvariantCulture));
        }
        return filter.ToString();
    }

    private static int? GetAgeSeconds(object creationDateValue)
    {
        if (creationDateValue == null)
        {
            return null;
        }

        try
        {
            DateTime creationDate = ManagementDateTimeConverter.ToDateTime(
                Convert.ToString(creationDateValue, CultureInfo.InvariantCulture));
            double seconds = Math.Max(0, (DateTime.Now - creationDate).TotalSeconds);
            return seconds >= Int32.MaxValue ? Int32.MaxValue : Convert.ToInt32(seconds);
        }
        catch
        {
            return null;
        }
    }

    private static string SerializeProcessDetails(List<ProcessDetails> details)
    {
        StringBuilder json = new StringBuilder(Math.Max(2, details.Count * 192));
        json.Append('[');
        for (int index = 0; index < details.Count; index++)
        {
            if (index > 0)
            {
                json.Append(',');
            }

            ProcessDetails item = details[index];
            json.Append("{\"ProcessId\":");
            json.Append(item.ProcessId.ToString(CultureInfo.InvariantCulture));
            json.Append(",\"ParentProcessId\":");
            json.Append(item.ParentProcessId.ToString(CultureInfo.InvariantCulture));
            json.Append(",\"CommandLine\":");
            AppendJsonString(json, item.CommandLine);
            json.Append(",\"WorkingSetSize\":");
            AppendNullableNumber(json, item.WorkingSetSize);
            json.Append(",\"CpuPercent\":");
            AppendNullableNumber(json, item.CpuPercent);
            json.Append(",\"AgeSeconds\":");
            AppendNullableNumber(json, item.AgeSeconds);
            json.Append('}');
        }
        json.Append(']');
        return json.ToString();
    }

    private static int RunDesktopMetadata(string encodedFallback)
    {
        if (!IsEnvironmentFlagEnabled(DisableOptimizationsVariable))
        {
            try
            {
                int rowCount;
                WriteStandardOutput(BuildDesktopMetadataJson(out rowCount));
                return 0;
            }
            catch (Exception error)
            {
                if (IsEnvironmentFlagEnabled(RequireOptimizationsVariable))
                {
                    Console.Error.WriteLine("Codex Windows desktop metadata optimization failed: " + error.Message);
                    return 9010;
                }
            }
        }

        return ForwardDesktopMetadataToPowerShell(encodedFallback);
    }

    private static string BuildDesktopMetadataJson(out int rowCount)
    {
        List<UserAssistEntry> userAssistEntries = GetUserAssistEntries();
        HashSet<string> runningProcessKeys = new HashSet<string>(StringComparer.Ordinal);
        List<RunningProcess> runningProcesses = GetRunningProcesses(runningProcessKeys);
        List<DesktopApp> apps = new List<DesktopApp>();

        foreach (StartApp startApp in GetStartApps())
        {
            if (String.IsNullOrWhiteSpace(startApp.Name) || String.IsNullOrWhiteSpace(startApp.AppId) ||
                startApp.AppId.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                startApp.AppId.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                startApp.AppId.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            UserAssistEntry bestMatch = FindBestUserAssistMatch(
                startApp.Name,
                startApp.AppId,
                userAssistEntries);
            List<string> processKeys = GetAppProcessKeys(startApp.Name, startApp.AppId);
            bool isRunning = false;
            foreach (string processKey in processKeys)
            {
                if (runningProcessKeys.Contains(processKey))
                {
                    isRunning = true;
                    break;
                }
            }

            apps.Add(new DesktopApp
            {
                BundleId = startApp.AppId,
                DisplayName = startApp.Name,
                AppPath = startApp.AppId,
                IsRunning = isRunning,
                LastUsedDateRanking = bestMatch == null ? 0 : bestMatch.LastRunMilliseconds,
                ProcessKeys = processKeys,
                UseCount = bestMatch == null ? 0 : bestMatch.RunCount
            });
        }

        HashSet<string> claimedProcessKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (DesktopApp app in apps)
        {
            foreach (string processKey in app.ProcessKeys)
            {
                if (runningProcessKeys.Contains(processKey))
                {
                    claimedProcessKeys.Add(processKey);
                }
            }
        }

        foreach (RunningProcess process in runningProcesses)
        {
            if (claimedProcessKeys.Contains(process.Key))
            {
                continue;
            }
            string appId = NewProcessAppId(process.Name, process.Path);
            apps.Add(new DesktopApp
            {
                BundleId = appId,
                DisplayName = process.Name,
                AppPath = appId,
                IsRunning = true,
                LastUsedDateRanking = 0,
                ProcessKeys = new List<string> { process.Key },
                UseCount = 0
            });
        }

        rowCount = apps.Count;
        return SerializeDesktopApps(apps);
    }

    private static List<UserAssistEntry> GetUserAssistEntries()
    {
        List<UserAssistEntry> entries = new List<UserAssistEntry>();
        using (RegistryKey root = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist"))
        {
            if (root == null)
            {
                return entries;
            }
            foreach (string subkeyName in root.GetSubKeyNames())
            {
                using (RegistryKey count = root.OpenSubKey(subkeyName + @"\Count"))
                {
                    if (count == null)
                    {
                        continue;
                    }
                    foreach (string valueName in count.GetValueNames())
                    {
                        byte[] bytes = count.GetValue(
                            valueName,
                            null,
                            RegistryValueOptions.DoNotExpandEnvironmentNames) as byte[];
                        if (bytes == null)
                        {
                            continue;
                        }
                        entries.Add(new UserAssistEntry
                        {
                            Name = DecodeRot13(valueName),
                            RunCount = bytes.Length >= 8 ? BitConverter.ToInt32(bytes, 4) : 0,
                            LastRunMilliseconds = ReadUserAssistLastRun(bytes)
                        });
                    }
                }
            }
        }
        return entries;
    }

    private static long ReadUserAssistLastRun(byte[] bytes)
    {
        int offset = bytes.Length >= 68 ? 60 : (bytes.Length >= 16 ? 8 : -1);
        if (offset < 0)
        {
            return 0;
        }
        long fileTime = BitConverter.ToInt64(bytes, offset);
        if (fileTime <= 0)
        {
            return 0;
        }
        try
        {
            DateTime utc = DateTime.FromFileTime(fileTime).ToUniversalTime();
            DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            double milliseconds = (utc - epoch).TotalMilliseconds;
            return milliseconds <= 0 ? 0 : Convert.ToInt64(milliseconds);
        }
        catch
        {
            return 0;
        }
    }

    private static string DecodeRot13(string value)
    {
        char[] characters = value.ToCharArray();
        for (int index = 0; index < characters.Length; index++)
        {
            char character = characters[index];
            if (character >= 'a' && character <= 'z')
            {
                characters[index] = (char)('a' + ((character - 'a' + 13) % 26));
            }
            else if (character >= 'A' && character <= 'Z')
            {
                characters[index] = (char)('A' + ((character - 'A' + 13) % 26));
            }
        }
        return new string(characters);
    }

    private static List<StartApp> GetStartApps()
    {
        object shell = null;
        object folder = null;
        object items = null;
        List<StartApp> apps = new List<StartApp>();
        try
        {
            Type shellType = Type.GetTypeFromProgID("Shell.Application", true);
            shell = Activator.CreateInstance(shellType);
            dynamic shellDispatch = shell;
            folder = shellDispatch.NameSpace("shell:AppsFolder");
            if (folder == null)
            {
                throw new InvalidOperationException("Windows shell:AppsFolder is unavailable.");
            }
            dynamic folderDispatch = folder;
            items = folderDispatch.Items();
            dynamic itemsDispatch = items;
            int count = Convert.ToInt32(itemsDispatch.Count, CultureInfo.InvariantCulture);
            for (int index = 0; index < count; index++)
            {
                object item = null;
                try
                {
                    item = itemsDispatch.Item(index);
                    dynamic itemDispatch = item;
                    string name = Convert.ToString(itemDispatch.Name, CultureInfo.CurrentCulture);
                    string appId = Convert.ToString(
                        itemDispatch.ExtendedProperty("System.AppUserModel.ID"),
                        CultureInfo.InvariantCulture);
                    if (!String.IsNullOrWhiteSpace(name) && !String.IsNullOrWhiteSpace(appId))
                    {
                        apps.Add(new StartApp { Name = name, AppId = appId });
                    }
                }
                catch
                {
                }
                finally
                {
                    ReleaseComObject(item);
                }
            }
        }
        finally
        {
            ReleaseComObject(items);
            ReleaseComObject(folder);
            ReleaseComObject(shell);
        }
        return apps;
    }

    private static void ReleaseComObject(object value)
    {
        if (value == null || !Marshal.IsComObject(value))
        {
            return;
        }
        try
        {
            Marshal.FinalReleaseComObject(value);
        }
        catch
        {
        }
    }

    private static List<RunningProcess> GetRunningProcesses(HashSet<string> runningProcessKeys)
    {
        List<RunningProcess> rows = new List<RunningProcess>();
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                string name;
                try
                {
                    name = process.ProcessName;
                }
                catch
                {
                    continue;
                }
                if (String.IsNullOrWhiteSpace(name))
                {
                    continue;
                }
                string key = GetProcessKey(name);
                runningProcessKeys.Add(key);
                string processPath = null;
                try
                {
                    processPath = process.MainModule == null ? null : process.MainModule.FileName;
                }
                catch
                {
                }
                rows.Add(new RunningProcess { Name = name, Key = key, Path = processPath });
            }
        }

        rows.Sort(delegate(RunningProcess left, RunningProcess right)
        {
            int pathComparison = UserCultureComparer.Compare(left.Path, right.Path);
            return pathComparison != 0
                ? pathComparison
                : UserCultureComparer.Compare(left.Key, right.Key);
        });
        List<RunningProcess> unique = new List<RunningProcess>();
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (RunningProcess row in rows)
        {
            string identity = (row.Path ?? String.Empty) + "\0" + row.Key;
            if (seen.Add(identity))
            {
                unique.Add(row);
            }
        }
        return unique;
    }

    private static List<string> GetAppProcessKeys(string name, string appId)
    {
        HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal);
        keys.Add(GetProcessKey(name));
        foreach (Match match in ExecutableNamePattern.Matches(appId))
        {
            keys.Add(GetProcessKey(GetTargetProcessName(match.Value)));
        }
        List<string> output = new List<string>(keys);
        output.Sort(ProcessKeyComparer);
        return output;
    }

    private static UserAssistEntry FindBestUserAssistMatch(
        string name,
        string appId,
        List<UserAssistEntry> entries)
    {
        List<UserAssistMatch> matches = new List<UserAssistMatch>();
        string shortcutPattern = @"(^|[\\/])" + Regex.Escape(name) + @"\.lnk$";
        List<string> executableNames = new List<string>();
        foreach (Match match in ExecutableNamePattern.Matches(appId))
        {
            executableNames.Add(match.Value);
        }

        foreach (UserAssistEntry entry in entries)
        {
            if (String.Equals(entry.Name, appId, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(new UserAssistMatch { Entry = entry, Rank = 1 });
                continue;
            }
            if (Regex.IsMatch(entry.Name, shortcutPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                matches.Add(new UserAssistMatch { Entry = entry, Rank = 2 });
                continue;
            }
            foreach (string executableName in executableNames)
            {
                string pattern = @"(^|[\\/])" + Regex.Escape(executableName) + "$";
                if (Regex.IsMatch(entry.Name, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    matches.Add(new UserAssistMatch { Entry = entry, Rank = 3 });
                    break;
                }
            }
        }

        matches.Sort(delegate(UserAssistMatch left, UserAssistMatch right)
        {
            int comparison = left.Rank.CompareTo(right.Rank);
            if (comparison != 0) return comparison;
            comparison = right.Entry.LastRunMilliseconds.CompareTo(left.Entry.LastRunMilliseconds);
            if (comparison != 0) return comparison;
            comparison = right.Entry.RunCount.CompareTo(left.Entry.RunCount);
            return comparison != 0
                ? comparison
                : UserCultureComparer.Compare(left.Entry.Name, right.Entry.Name);
        });
        return matches.Count == 0 ? null : matches[0].Entry;
    }

    private static StringComparer CreateUserCultureComparer()
    {
        try
        {
            using (RegistryKey international = Registry.CurrentUser.OpenSubKey(@"Control Panel\International"))
            {
                string localeName = international == null
                    ? null
                    : international.GetValue("LocaleName") as string;
                if (!String.IsNullOrWhiteSpace(localeName))
                {
                    return StringComparer.Create(CultureInfo.GetCultureInfo(localeName), true);
                }
            }
        }
        catch
        {
        }
        return StringComparer.CurrentCultureIgnoreCase;
    }

    private static string GetProcessKey(string value)
    {
        string trimmed = (value ?? String.Empty).Trim().Trim('"').ToLowerInvariant();
        return trimmed.EndsWith(".exe", StringComparison.Ordinal)
            ? trimmed.Substring(0, trimmed.Length - 4)
            : trimmed;
    }

    private static string GetTargetProcessName(string value)
    {
        string trimmed = (value ?? String.Empty).Trim().Trim('"');
        if (trimmed.Length == 0)
        {
            return trimmed;
        }
        try
        {
            return Regex.Replace(
                Path.GetFileName(trimmed),
                @"\.exe$",
                String.Empty,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        catch
        {
            return Regex.Replace(
                trimmed,
                @"\.exe$",
                String.Empty,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
    }

    private static string NewProcessAppId(string processName, string processPath)
    {
        if (!String.IsNullOrWhiteSpace(processPath))
        {
            return "process:" + processPath.Trim();
        }
        return "process:" + GetProcessKey(GetTargetProcessName(processName)) + ".exe";
    }

    private static string SerializeDesktopApps(List<DesktopApp> apps)
    {
        StringBuilder json = new StringBuilder(Math.Max(2, apps.Count * 200));
        json.Append('[');
        for (int index = 0; index < apps.Count; index++)
        {
            if (index > 0)
            {
                json.Append(',');
            }
            DesktopApp app = apps[index];
            json.Append("{\"bundleId\":");
            AppendJsonString(json, app.BundleId);
            json.Append(",\"displayName\":");
            AppendJsonString(json, app.DisplayName);
            json.Append(",\"appPath\":");
            AppendJsonString(json, app.AppPath);
            json.Append(",\"isRunning\":");
            json.Append(app.IsRunning ? "true" : "false");
            json.Append(",\"lastUsedDateRanking\":");
            json.Append(app.LastUsedDateRanking.ToString(CultureInfo.InvariantCulture));
            json.Append(",\"processKeys\":");
            AppendJsonStringArray(json, app.ProcessKeys);
            json.Append(",\"useCount\":");
            json.Append(app.UseCount.ToString(CultureInfo.InvariantCulture));
            json.Append('}');
        }
        json.Append(']');
        return json.ToString();
    }

    private static void AppendJsonStringArray(StringBuilder json, List<string> values)
    {
        json.Append('[');
        for (int index = 0; index < values.Count; index++)
        {
            if (index > 0)
            {
                json.Append(',');
            }
            AppendJsonString(json, values[index]);
        }
        json.Append(']');
    }

    private static string BuildZipListing(string archivePath, out int rowCount)
    {
        StringBuilder output = new StringBuilder();
        using (ZipArchive archive = ZipFile.OpenRead(Path.GetFullPath(archivePath)))
        {
            rowCount = archive.Entries.Count;
            for (int index = 0; index < archive.Entries.Count; index++)
            {
                if (index > 0)
                {
                    output.Append(Environment.NewLine);
                }
                output.Append(archive.Entries[index].FullName);
            }
        }
        return output.ToString();
    }

    private static void ExtractZipArchive(string archivePath, string destinationPath, out int rowCount)
    {
        string destinationRoot = Path.GetFullPath(destinationPath).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        string destinationPrefix = destinationRoot + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(destinationRoot);
        rowCount = 0;

        using (ZipArchive archive = ZipFile.OpenRead(Path.GetFullPath(archivePath)))
        {
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string relativePath = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                string target = Path.GetFullPath(Path.Combine(destinationRoot, relativePath));
                if (!String.Equals(target, destinationRoot, StringComparison.OrdinalIgnoreCase) &&
                    !target.StartsWith(destinationPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("ZIP entry escapes the extraction directory: " + entry.FullName);
                }

                bool isDirectory = String.IsNullOrEmpty(entry.Name) ||
                    entry.FullName.EndsWith("/", StringComparison.Ordinal) ||
                    entry.FullName.EndsWith("\\", StringComparison.Ordinal);
                if (isDirectory)
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                string parent = Path.GetDirectoryName(target);
                if (!String.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }
                using (Stream input = entry.Open())
                using (FileStream output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    input.CopyTo(output);
                }
                try
                {
                    File.SetLastWriteTime(target, entry.LastWriteTime.LocalDateTime);
                }
                catch
                {
                }
                rowCount++;
            }
        }
    }

    private static void RunZipSelfTest(out int rowCount)
    {
        string root = Path.Combine(Path.GetTempPath(), "codex-helper-self-test-" + Guid.NewGuid().ToString("N"));
        string archivePath = Path.Combine(root, "probe.zip");
        string destinationPath = Path.Combine(root, "extracted");
        Directory.CreateDirectory(root);
        try
        {
            using (ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                ZipArchiveEntry entry = archive.CreateEntry("folder/probe.txt");
                using (StreamWriter writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)))
                {
                    writer.Write("codex-helper-probe");
                }
            }
            int listedRows;
            string listing = BuildZipListing(archivePath, out listedRows);
            ExtractZipArchive(archivePath, destinationPath, out rowCount);
            string extracted = File.ReadAllText(Path.Combine(destinationPath, "folder", "probe.txt"));
            if (listedRows != 1 || !listing.Contains("folder/probe.txt") || extracted != "codex-helper-probe")
            {
                throw new InvalidDataException("ZIP self-test output did not match the input.");
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static void AppendJsonString(StringBuilder json, string value)
    {
        if (value == null)
        {
            json.Append("null");
            return;
        }

        json.Append('"');
        foreach (char character in value)
        {
            switch (character)
            {
                case '"': json.Append("\\\""); break;
                case '\\': json.Append("\\\\"); break;
                case '\b': json.Append("\\b"); break;
                case '\f': json.Append("\\f"); break;
                case '\n': json.Append("\\n"); break;
                case '\r': json.Append("\\r"); break;
                case '\t': json.Append("\\t"); break;
                default:
                    if (character < 0x20 || character > 0x7e)
                    {
                        json.Append("\\u");
                        json.Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        json.Append(character);
                    }
                    break;
            }
        }
        json.Append('"');
    }

    private static void AppendNullableNumber(StringBuilder json, ulong? value)
    {
        json.Append(value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "null");
    }

    private static void AppendNullableNumber(StringBuilder json, int? value)
    {
        json.Append(value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "null");
    }

    private static void AppendNullableNumber(StringBuilder json, double? value)
    {
        if (!value.HasValue || Double.IsNaN(value.Value) || Double.IsInfinity(value.Value))
        {
            json.Append("null");
        }
        else
        {
            json.Append(value.Value.ToString("R", CultureInfo.InvariantCulture));
        }
    }

    private static void WriteStandardOutput(string value)
    {
        byte[] bytes = new UTF8Encoding(false).GetBytes(value + Environment.NewLine);
        Stream output = Console.OpenStandardOutput();
        output.Write(bytes, 0, bytes.Length);
        output.Flush();
    }

    private static bool IsEnvironmentFlagEnabled(string name)
    {
        string value = Environment.GetEnvironmentVariable(name);
        return String.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
            String.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
            String.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolvePowerShellExecutable()
    {
        string configured = NormalizeConfiguredPath(
            Environment.GetEnvironmentVariable(PreferredPowerShellVariable));
        if (!String.IsNullOrWhiteSpace(configured))
        {
            if (IsUsablePowerShell(configured))
            {
                return Path.GetFullPath(configured);
            }
            throw new FileNotFoundException(
                PreferredPowerShellVariable + " does not point to a usable executable.",
                configured);
        }

        string distributionDirectory = GetDistributionDirectory();
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] fixedCandidates = new string[]
        {
            Path.Combine(distributionDirectory, "pwsh", "pwsh.exe"),
            CombineIfRooted(Environment.GetEnvironmentVariable("ProgramW6432"), @"PowerShell\7\pwsh.exe"),
            CombineIfRooted(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"PowerShell\7\pwsh.exe"),
            FindPortablePowerShell(localAppData),
            FindOnPath("pwsh.exe"),
            CombineIfRooted(localAppData, @"Microsoft\WindowsApps\pwsh.exe"),
            Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe")
        };

        foreach (string candidate in fixedCandidates)
        {
            if (IsUsablePowerShell(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    private static string GetDistributionDirectory()
    {
        string baseDirectory = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        if (String.Equals(
            Path.GetFileName(baseDirectory),
            LauncherDirectoryName,
            StringComparison.OrdinalIgnoreCase))
        {
            DirectoryInfo parent = Directory.GetParent(baseDirectory);
            if (parent != null)
            {
                return parent.FullName;
            }
        }
        return baseDirectory;
    }

    private static bool IsTransparentLauncher()
    {
        try
        {
            return IsPrivateLauncherPath(Assembly.GetExecutingAssembly().Location);
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeConfiguredPath(string value)
    {
        if (String.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[trimmed.Length - 1] == '"')
        {
            trimmed = trimmed.Substring(1, trimmed.Length - 2);
        }
        return trimmed;
    }

    private static string CombineIfRooted(string root, string relativePath)
    {
        return String.IsNullOrWhiteSpace(root) ? null : Path.Combine(root, relativePath);
    }

    private static string FindPortablePowerShell(string localAppData)
    {
        if (String.IsNullOrWhiteSpace(localAppData))
        {
            return null;
        }

        string root = Path.Combine(localAppData, @"CodexPwshRuntime\PowerShell");
        if (!Directory.Exists(root))
        {
            return null;
        }

        List<string> candidates = new List<string>();
        foreach (string directory in Directory.GetDirectories(root))
        {
            string candidate = Path.Combine(directory, "pwsh.exe");
            if (IsUsablePowerShell(candidate))
            {
                candidates.Add(candidate);
            }
        }
        candidates.Sort(delegate(string left, string right)
        {
            Version leftVersion = GetPortablePowerShellVersion(left);
            Version rightVersion = GetPortablePowerShellVersion(right);
            int versionComparison = leftVersion.CompareTo(rightVersion);
            return versionComparison != 0
                ? versionComparison
                : StringComparer.OrdinalIgnoreCase.Compare(left, right);
        });
        return candidates.Count == 0 ? null : candidates[candidates.Count - 1];
    }

    private static Version GetPortablePowerShellVersion(string executablePath)
    {
        string directoryName = Path.GetFileName(Path.GetDirectoryName(executablePath));
        int suffixIndex = directoryName.IndexOf('-');
        if (suffixIndex > 0)
        {
            directoryName = directoryName.Substring(0, suffixIndex);
        }

        Version version;
        return Version.TryParse(directoryName, out version) ? version : new Version(0, 0);
    }

    private static string FindOnPath(string executableName)
    {
        string pathValue = Environment.GetEnvironmentVariable("PATH");
        if (String.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        foreach (string entry in pathValue.Split(Path.PathSeparator))
        {
            string directory = NormalizeConfiguredPath(entry);
            if (String.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            string candidate;
            try
            {
                candidate = Path.Combine(directory, executableName);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (IsUsablePowerShell(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    private static bool IsUsablePowerShell(string candidate)
    {
        if (String.IsNullOrWhiteSpace(candidate) || !File.Exists(candidate))
        {
            return false;
        }

        try
        {
            string helper = Path.GetFullPath(Assembly.GetExecutingAssembly().Location);
            string target = Path.GetFullPath(candidate);
            return !String.Equals(helper, target, StringComparison.OrdinalIgnoreCase) &&
                !IsPrivateLauncherPath(target);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPrivateLauncherPath(string executablePath)
    {
        string filename = Path.GetFileName(executablePath);
        string directoryName = Path.GetFileName(Path.GetDirectoryName(executablePath));
        return String.Equals(
                directoryName,
                LauncherDirectoryName,
                StringComparison.OrdinalIgnoreCase) &&
            (String.Equals(
                filename,
                WindowsPowerShellLauncherFilename,
                StringComparison.OrdinalIgnoreCase) ||
            String.Equals(
                filename,
                PowerShell7LauncherFilename,
                StringComparison.OrdinalIgnoreCase));
    }

    private static int ForwardDesktopMetadataToPowerShell(string encodedCommand)
    {
        if (String.IsNullOrWhiteSpace(encodedCommand))
        {
            Console.Error.WriteLine("Codex Windows desktop metadata fallback command is missing.");
            return 9010;
        }
        try
        {
            Convert.FromBase64String(encodedCommand);
        }
        catch (FormatException)
        {
            Console.Error.WriteLine("Codex Windows desktop metadata fallback command is invalid.");
            return 9010;
        }
        return ForwardToPowerShellArguments(
            false,
            "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encodedCommand);
    }

    private static int ForwardToPowerShell(bool transparent)
    {
        return ForwardToPowerShellArguments(transparent, GetRawArgumentTail(Environment.CommandLine));
    }

    private static int ForwardToPowerShellArguments(bool transparent, string arguments)
    {
        string component = transparent
            ? "Codex PowerShell launcher"
            : "Codex PowerShell query helper";
        IntPtr job = IntPtr.Zero;
        Process child = null;

        try
        {
            string powerShell = ResolvePowerShellExecutable();
            if (String.IsNullOrWhiteSpace(powerShell))
            {
                Console.Error.WriteLine(component + ": no PowerShell executable was found.");
                return 9009;
            }

            job = CreateKillOnCloseJob();
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = powerShell,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = !transparent,
                WorkingDirectory = Environment.CurrentDirectory,
                RedirectStandardOutput = !transparent,
                RedirectStandardError = !transparent
            };

            if (!startInfo.EnvironmentVariables.ContainsKey("POWERSHELL_TELEMETRY_OPTOUT"))
            {
                startInfo.EnvironmentVariables["POWERSHELL_TELEMETRY_OPTOUT"] = "1";
            }
            if (!startInfo.EnvironmentVariables.ContainsKey("POWERSHELL_UPDATECHECK"))
            {
                startInfo.EnvironmentVariables["POWERSHELL_UPDATECHECK"] = "Off";
            }

            child = Process.Start(startInfo);
            if (child == null)
            {
                Console.Error.WriteLine(component + ": failed to start the resolved shell.");
                return 9009;
            }
            PowerShellLaunchCount++;

            if (job != IntPtr.Zero && !AssignProcessToJobObject(job, child.Handle))
            {
                CloseHandle(job);
                job = IntPtr.Zero;
            }

            Task stdoutCopy = null;
            Task stderrCopy = null;
            if (!transparent)
            {
                stdoutCopy = child.StandardOutput.BaseStream.CopyToAsync(Console.OpenStandardOutput());
                stderrCopy = child.StandardError.BaseStream.CopyToAsync(Console.OpenStandardError());
            }
            child.WaitForExit();
            if (!transparent)
            {
                Task.WaitAll(stdoutCopy, stderrCopy);
            }
            return child.ExitCode;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(component + ": " + error.Message);
            return error is Win32Exception ? ((Win32Exception)error).NativeErrorCode : 1;
        }
        finally
        {
            if (child != null)
            {
                child.Dispose();
            }
            if (job != IntPtr.Zero)
            {
                CloseHandle(job);
            }
        }
    }

    private static IntPtr CreateKillOnCloseJob()
    {
        IntPtr job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        JobObjectExtendedLimitInformation info = new JobObjectExtendedLimitInformation();
        info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
        uint size = (uint)Marshal.SizeOf(typeof(JobObjectExtendedLimitInformation));
        if (!SetInformationJobObject(job, JobObjectInfoType.ExtendedLimitInformation, ref info, size))
        {
            CloseHandle(job);
            return IntPtr.Zero;
        }
        return job;
    }

    private static string GetRawArgumentTail(string commandLine)
    {
        if (String.IsNullOrWhiteSpace(commandLine))
        {
            return String.Empty;
        }

        int index = 0;
        while (index < commandLine.Length && Char.IsWhiteSpace(commandLine[index]))
        {
            index++;
        }

        if (index < commandLine.Length && commandLine[index] == '"')
        {
            index++;
            while (index < commandLine.Length && commandLine[index] != '"')
            {
                index++;
            }
            if (index < commandLine.Length)
            {
                index++;
            }
        }
        else
        {
            while (index < commandLine.Length && !Char.IsWhiteSpace(commandLine[index]))
            {
                index++;
            }
        }

        while (index < commandLine.Length && Char.IsWhiteSpace(commandLine[index]))
        {
            index++;
        }
        return index < commandLine.Length ? commandLine.Substring(index) : String.Empty;
    }
}
