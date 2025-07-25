﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using BenchmarkDotNet.Detectors;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Extensions;
using BenchmarkDotNet.Helpers;
using static System.Runtime.InteropServices.RuntimeInformation;

namespace BenchmarkDotNet.Portability
{
    internal static class RuntimeInformation
    {
        internal const string DebugConfigurationName = "DEBUG";
        internal const string ReleaseConfigurationName = "RELEASE";
        internal const string Unknown = "?";

        // Many of these checks allocate and/or are expensive to compute. We store the results in static readonly fields to keep Engine non-allocating.
        // Static readonly fields are used instead of properties to avoid an extra getter method call that might not be tier1 jitted.
        // This class is internal, so we don't need to expose these as properties.

        /// <summary>
        /// returns true for both the old (implementation of .NET Framework) and new Mono (.NET 6+ flavour)
        /// </summary>
        public static readonly bool IsMono = Type.GetType("Mono.RuntimeStructs") != null;

        public static readonly bool IsOldMono = Type.GetType("Mono.Runtime") != null;

        public static readonly bool IsNewMono = IsMono && !IsOldMono;

        public static readonly bool IsFullFramework =
#if NET6_0_OR_GREATER
            // This could be const, but we want to avoid unreachable code warnings.
            false;
#else
            FrameworkDescription.StartsWith(".NET Framework", StringComparison.OrdinalIgnoreCase);
#endif

#if NET6_0_OR_GREATER
        [System.Runtime.Versioning.SupportedOSPlatformGuard("browser")]
        public static readonly bool IsWasm = OperatingSystem.IsBrowser();
#else
        public static readonly bool IsWasm = IsOSPlatform(OSPlatform.Create("BROWSER"));
#endif

#if NETSTANDARD2_0
        public static readonly bool IsAot = IsAotMethod() || FrameworkDescription.StartsWith(".NET Native", StringComparison.OrdinalIgnoreCase);

        private static bool IsAotMethod()
        {
            Type runtimeFeature = Type.GetType("System.Runtime.CompilerServices.RuntimeFeature");
            if (runtimeFeature != null)
            {
                MethodInfo methodInfo = runtimeFeature.GetProperty("IsDynamicCodeCompiled", BindingFlags.Public | BindingFlags.Static)?.GetMethod;

                if (methodInfo != null)
                {
                    return !(bool)methodInfo.Invoke(null, null);
                }
            }

            return false;
        }
#else
        public static readonly bool IsAot = !System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeCompiled;
#endif

        public static bool IsNetCore
            => ((Environment.Version.Major >= 5) || FrameworkDescription.StartsWith(".NET Core", StringComparison.OrdinalIgnoreCase))
                && !IsAot;

        public static bool IsNativeAOT
            => Environment.Version.Major >= 5
               && IsAot
               && !IsWasm && !IsMono; // Wasm and MonoAOTLLVM are also AOT


        public static readonly bool IsTieredJitEnabled =
            IsNetCore
            && (Environment.Version.Major < 3
                // Disabled by default in netcoreapp2.X, check if it's enabled.
                ? Environment.GetEnvironmentVariable("COMPlus_TieredCompilation") == "1"
                || Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") == "1"
                || (AppContext.TryGetSwitch("System.Runtime.TieredCompilation", out bool isEnabled) && isEnabled)
                // Enabled by default in netcoreapp3.0+, check if it's disabled.
                : Environment.GetEnvironmentVariable("COMPlus_TieredCompilation") != "0"
                && Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") != "0"
                && (!AppContext.TryGetSwitch("System.Runtime.TieredCompilation", out isEnabled) || isEnabled));

        public static readonly bool IsRunningInContainer = string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true");

        internal static string GetArchitecture() => GetCurrentPlatform().ToString();

        internal static string GetRuntimeVersion()
        {
            if (IsWasm)
            {
                // code copied from https://github.com/dotnet/runtime/blob/2c573b59aaaf3fd17e2ecab95ad3769f195d2dbc/src/libraries/System.Runtime.InteropServices.RuntimeInformation/src/System/Runtime/InteropServices/RuntimeInformation/RuntimeInformation.cs#L20-L30
                string versionString = typeof(object).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

                // Strip the git hash if there is one
                if (versionString != null)
                {
                    int plusIndex = versionString.IndexOf('+');
                    if (plusIndex != -1)
                    {
                        versionString = versionString.Substring(0, plusIndex);
                    }
                }

                return $".NET Core (Mono) {versionString}";
            }
            else if (IsOldMono)
            {
                var monoRuntimeType = Type.GetType("Mono.Runtime");
                var monoDisplayName = monoRuntimeType?.GetMethod("GetDisplayName", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (monoDisplayName != null)
                {
                    string version = monoDisplayName.Invoke(null, null)?.ToString();
                    if (version != null)
                    {
                        int bracket1 = version.IndexOf('('), bracket2 = version.IndexOf(')');
                        if (bracket1 != -1 && bracket2 != -1)
                        {
                            string comment = version.Substring(bracket1 + 1, bracket2 - bracket1 - 1);
                            var commentParts = comment.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                            if (commentParts.Length > 2)
                                version = version.Substring(0, bracket1) + "(" + commentParts[0] + " " + commentParts[1] + ")";
                        }
                    }

                    return "Mono " + version;
                }
            }
            else if (IsNewMono)
            {
                return $"{GetNetCoreVersion()} using MonoVM";
            }
            else if (IsFullFramework)
            {
                return FrameworkVersionHelper.GetFrameworkDescription();
            }
            else if (IsNetCore)
            {
                return GetNetCoreVersion();
            }
            else if (IsNativeAOT)
            {
                return FrameworkDescription;
            }

            return Unknown;
        }

        private static string GetNetCoreVersion()
        {
            if (OsDetector.IsAndroid())
            {
                return $".NET {Environment.Version}";
            }

            return CoreRuntime.TryGetVersion(out var version) && version.Major >= 5
                ? $".NET {version} ({GetDetailedVersion()})"
                : $".NET Core {version?.ToString() ?? Unknown} ({GetDetailedVersion()})";

            string GetDetailedVersion()
            {
                string coreclrLocation = typeof(object).GetTypeInfo().Assembly.Location;
                // Single-file publish has empty assembly location.
                if (string.IsNullOrEmpty(coreclrLocation))
                    return CoreRuntime.GetVersionFromFrameworkDescription();
                // .Net Core 2.X has confusing FrameworkDescription like 4.6.X.
                if (version?.Major >= 3)
                    return $"{CoreRuntime.GetVersionFromFrameworkDescription()}, {FileVersionInfo.GetVersionInfo(coreclrLocation).FileVersion}";
                return FileVersionInfo.GetVersionInfo(coreclrLocation).FileVersion;
            }
        }

        internal static Runtime GetCurrentRuntime()
        {
            //do not change the order of conditions because it may cause incorrect determination of runtime
            if (IsAot && IsMono)
                return MonoAotLLVMRuntime.Default;
            if (IsWasm)
                return WasmRuntime.Default;
            if (IsNewMono)
                return MonoRuntime.GetCurrentVersion();
            if (IsOldMono)
                return MonoRuntime.Default;
            if (IsFullFramework)
                return ClrRuntime.GetCurrentVersion();
            if (IsNetCore)
                return CoreRuntime.GetCurrentVersion();
            if (IsNativeAOT)
                return NativeAotRuntime.GetCurrentVersion();

            throw new NotSupportedException("Unknown .NET Runtime");
        }

        public static Platform GetCurrentPlatform()
        {
            // these are not part of .NET Standard 2.0, so we use hardcoded values taken from
            // https://github.com/dotnet/runtime/blob/080fcae7eaa8367abf7900e08ff2e52e3efea5bf/src/libraries/System.Private.CoreLib/src/System/Runtime/InteropServices/Architecture.cs#L9
            const Architecture Wasm = (Architecture)4;
            const Architecture S390x = (Architecture)5;
            const Architecture LoongArch64 = (Architecture)6;
            const Architecture Armv6 = (Architecture)7;
            const Architecture Ppc64le = (Architecture)8;
            const Architecture RiscV64 = (Architecture)9;

            switch (ProcessArchitecture)
            {
                case Architecture.Arm:
                    return Platform.Arm;
                case Architecture.Arm64:
                    return Platform.Arm64;
                case Architecture.X64:
                    return Platform.X64;
                case Architecture.X86:
                    return Platform.X86;
                case Wasm:
                    return Platform.Wasm;
                case S390x:
                    return Platform.S390x;
                case LoongArch64:
                    return Platform.LoongArch64;
                case Armv6:
                    return Platform.Armv6;
                case Ppc64le:
                    return Platform.Ppc64le;
                case RiscV64:
                    return Platform.RiscV64;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        public static bool Is64BitPlatform() => IntPtr.Size == 8;

        internal static bool HasRyuJit()
        {
            if (IsMono)
                return false;
            if (IsNetCore)
                return true;

            return Is64BitPlatform()
                   && GetConfiguration() != DebugConfigurationName
                   && !new JitHelper().IsMsX64();
        }

        internal static Jit GetCurrentJit() => HasRyuJit() ? Jit.RyuJit : Jit.LegacyJit;

        internal static string GetJitInfo()
        {
            if (IsNativeAOT)
                return "NativeAOT";
            if (IsAot)
                return "AOT";
            if (IsMono || IsWasm)
                return ""; // There is no helpful information about JIT on Mono
            if (IsNetCore || HasRyuJit()) // CoreCLR supports only RyuJIT
                return "RyuJIT";
            if (IsFullFramework)
                return "LegacyJIT";

            return Unknown;
        }

        internal static IntPtr GetCurrentAffinity() => Process.GetCurrentProcess().TryGetAffinity() ?? default;

        internal static string GetConfiguration()
        {
            var isDebug = Assembly.GetEntryAssembly().IsDebug();
            if (isDebug.HasValue == false)
            {
                return Unknown;
            }
            return isDebug.Value ? DebugConfigurationName : ReleaseConfigurationName;
        }

        // See http://aakinshin.net/en/blog/dotnet/jit-version-determining-in-runtime/
        private class JitHelper
        {
            [SuppressMessage("IDE0052", "IDE0052")]
            [SuppressMessage("IDE0079", "IDE0079")]
            [SuppressMessage("ReSharper", "NotAccessedField.Local")]
            private int bar;

            public bool IsMsX64(int step = 1)
            {
                int value = 0;
                for (int i = 0; i < step; i++)
                {
                    bar = i + 10;
                    for (int j = 0; j < 2 * step; j += step)
                        value = j + 10;
                }
                return value == 20 + step;
            }
        }

        private class JitModule
        {
            public string Name { get; }
            public string Version { get; }

            public JitModule(string name, string version)
            {
                Name = name;
                Version = version;
            }
        }

        internal static ICollection<Antivirus> GetAntivirusProducts()
        {
            var products = new List<Antivirus>();
            if (OsDetector.IsWindows())
            {
                try
                {
                    using (var wmi = new ManagementObjectSearcher(@"root\SecurityCenter2", "SELECT * FROM AntiVirusProduct"))
                    using (var data = wmi.Get())
                        foreach (var o in data)
                        {
                            var av = (ManagementObject)o;
                            if (av != null)
                            {
                                string name = av["displayName"].ToString();
                                string path = av["pathToSignedProductExe"].ToString();
                                products.Add(new Antivirus(name, path));
                            }
                        }
                }
                catch
                {
                    // Never mind
                }
            }

            return products;
        }

        internal static VirtualMachineHypervisor? GetVirtualMachineHypervisor()
        {
            VirtualMachineHypervisor[] hypervisors = [HyperV.Default, VirtualBox.Default, VMware.Default];

            if (OsDetector.IsWindows())
            {
                try
                {
                    using (var searcher = new ManagementObjectSearcher("Select * from Win32_ComputerSystem"))
                    {
                        using (var items = searcher.Get())
                        {
                            foreach (var item in items)
                            {
                                string manufacturer = item["Manufacturer"]?.ToString();
                                string model = item["Model"]?.ToString();
                                return hypervisors.FirstOrDefault(x => x.IsVirtualMachine(manufacturer, model));
                            }
                        }
                    }
                }
                catch
                {
                    // Never mind
                }
            }

            return null;
        }
    }
}