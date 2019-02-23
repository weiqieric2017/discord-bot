﻿using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CompatApiClient.Utils;
using CompatBot.EventHandlers.LogParsing.POCOs;
using DSharpPlus;
using DSharpPlus.Entities;
using IrdLibraryClient.IrdFormat;

namespace CompatBot.Utils.ResultFormatters
{
    internal static partial class LogParserResult
    {
        private static async Task BuildNotesSectionAsync(DiscordEmbedBuilder builder, LogParseState state, NameValueCollection items, DiscordClient discordClient)
        {
            BuildWeirdSettingsSection(builder, items);
            BuildMissingLicensesSection(builder, items);
            var (irdChecked, brokenDump) = await HasBrokenFilesAsync(items).ConfigureAwait(false);
            brokenDump |= !string.IsNullOrEmpty(items["edat_block_offset"]);
            var elfBootPath = items["elf_boot_path"] ?? "";
            var isEboot = !string.IsNullOrEmpty(elfBootPath) && elfBootPath.EndsWith("EBOOT.BIN", StringComparison.InvariantCultureIgnoreCase);
            var isElf = !string.IsNullOrEmpty(elfBootPath) && !elfBootPath.EndsWith("EBOOT.BIN", StringComparison.InvariantCultureIgnoreCase);
            var notes = new List<string>();
            if (items["fatal_error"] is string fatalError)
            {
                builder.AddField("Fatal Error", $"```{fatalError.Trim(1022)}```");
                if (fatalError.Contains("psf.cpp") || fatalError.Contains("invalid map<K, T>"))
                    notes.Add("⚠ Game save data might be corrupted");
                else if (fatalError.Contains("Could not bind OpenGL context"))
                    notes.Add("❌ GPU or installed GPU drivers do not support OpenGL 4.3");
            }

            if (items["failed_to_decrypt"] is string _)
                notes.Add("❌ Failed to decrypt game content, license file might be corrupted");
            if (items["failed_to_boot"] is string _)
                notes.Add("❌ Failed to boot the game, the dump might be encrypted or corrupted");
            if (brokenDump)
                notes.Add("❌ Some game files are missing or corrupted, please re-dump and validate.");
            else if (irdChecked)
                notes.Add("✅ Checked missing files against IRD");
            if (items["fw_version_installed"] is string fw && !string.IsNullOrEmpty(fw))
            {
                if (Version.TryParse(fw, out var fwv))
                {
                    if (fwv < MinimumFirmwareVersion)
                        notes.Add($"⚠ Firmware version {MinimumFirmwareVersion} or later is recommended");
                }
                else
                    notes.Add("⚠ Custom firmware is not supported, please use the latest official one");
            }

            if (!string.IsNullOrEmpty(items["host_root_in_boot"]) && isEboot)
                notes.Add("❌ Retail game booted as an ELF through the `/root_host/`, probably due to passing path as an argument; please boot through the game library list for now");
            if (!string.IsNullOrEmpty(items["serial"]) && isElf)
                notes.Add($"⚠ Retail game booted directly through `{Path.GetFileName(elfBootPath)}`, which is not recommended");
            if (string.IsNullOrEmpty(items["serial"] + items["game_title"]) &&
                items["fw_version_installed"] is string fwVersion)
            {
                notes.Add($"ℹ The log contains only installation of firmware {fwVersion}");
                notes.Add("ℹ Please boot the game and upload a new log");
            }

            if (string.IsNullOrEmpty(items["ppu_decoder"]) || string.IsNullOrEmpty(items["renderer"]))
            {
                notes.Add("ℹ The log is empty");
                notes.Add("ℹ Please boot the game and upload a new log");
            }

            if (items["cpu_model"] is string cpu)
            {
                if (cpu.StartsWith("AMD"))
                {
                    if (cpu.Contains("Ryzen"))
                    {
                        if (items["os_path"] != "Linux"
                            && items["thread_scheduler"] == DisabledMark)
                            notes.Add("⚠ Please enable `Thread scheduler` option in the CPU Settings");
                    }
                    else
                        notes.Add("⚠ AMD CPUs before Ryzen are too weak for PS3 emulation");
                }

                if (cpu.StartsWith("Intel"))
                {
                    if (cpu.Contains("Core2")
                        || cpu.Contains("Celeron")
                        || cpu.Contains("Atom")
                        || cpu.Contains("Pentium"))
                        notes.Add("⚠ This CPU is too old and/or too weak for PS3 emulation");
                }
            }

            if (int.TryParse(items["thread_count"], out var threadCount) && threadCount < 4)
                notes.Add($"⚠ This CPU only has {threadCount} hardware thread{(threadCount == 1 ? "" : "s")} enabled");

            var supportedGpu = true;
            Version oglVersion = null;
            if (items["opengl_version"] is string oglVersionString)
                Version.TryParse(oglVersionString, out oglVersion);
            if (items["glsl_version"] is string glslVersionString &&
                Version.TryParse(glslVersionString, out var glslVersion))
            {
                glslVersion = new Version(glslVersion.Major, glslVersion.Minor / 10);
                if (oglVersion == null || glslVersion > oglVersion)
                    oglVersion = glslVersion;
            }

            if (oglVersion != null)
            {
                if (oglVersion < MinimumOpenGLVersion)
                {
                    notes.Add($"❌ GPU only supports OpenGL {oglVersion.Major}.{oglVersion.Minor}, which is below the minimum requirement of {MinimumOpenGLVersion}");
                    supportedGpu = false;
                }
            }

            if (supportedGpu
                && items["gpu_info"] is string gpuInfo)
            {
                if (IntelGpuModel.Match(gpuInfo) is Match intelMatch
                    && intelMatch.Success)
                {
                    var modelNumber = intelMatch.Groups["gpu_model_number"].Value;
                    if (!string.IsNullOrEmpty(modelNumber) && modelNumber.StartsWith('P'))
                        modelNumber = modelNumber.Substring(1);
                    int.TryParse(modelNumber, out var modelNumberInt);
                    if (modelNumberInt < 500 || modelNumberInt > 1000)
                    {
                        notes.Add("❌ Intel iGPUs before Skylake do not fully comply with OpenGL 4.3");
                        supportedGpu = false;
                    }
                    else
                        notes.Add("⚠ Intel iGPUs are not officially supported, visual glitches are to be expected");
                }

                if (items["os_path"] is string os
                    && os != "Linux"
                    && IsNvidia(gpuInfo)
                    && items["driver_version_info"] is string driverVersionString
                    && Version.TryParse(driverVersionString, out var driverVersion))
                {
                    if (driverVersion < NvidiaRecommendedOldWindowsVersion)
                        notes.Add($"❗ Please update your nVidia driver to at least {NvidiaRecommendedOldWindowsVersion}");
                    if (driverVersion >= NvidiaFullscreenBugMinVersion
                        && driverVersion < NvidiaFullscreenBugMaxVersion
                        && items["renderer"] == "Vulkan"
                        && items["vsync"] == DisabledMark)
                        notes.Add("⚠ **400 series** nVidia drivers can cause random screen freeze when playing in **fullscreen** using **Vulkan** renderer with **vsync disabled**");
                }
            }

            if (!string.IsNullOrEmpty(items["shader_compile_error"]))
            {
                if (supportedGpu)
                    notes.Add("❌ Shader compilation error might indicate shader cache corruption");
                else
                    notes.Add("❌ Shader compilation error on unsupported GPU");
            }

            if (!string.IsNullOrEmpty(items["ppu_hash_patch"]) || !string.IsNullOrEmpty(items["spu_hash_patch"]))
                notes.Add("ℹ Game-specific patches were applied");

            if (items["serial"] is string serial
                && KnownDisableVertexCacheIds.Contains(serial))
            {
                if (items["vertex_cache"] == DisabledMark)
                    notes.Add("⚠ This game requires disabling `Vertex Cache` in the GPU tab of the Settings");
            }

            bool discInsideGame = false;
            bool discAsPkg = false;
            if (items["game_category"] == "DG")
            {
                discInsideGame |= !string.IsNullOrEmpty(items["ldr_disc"]) && !(items["serial"]?.StartsWith("NP", StringComparison.InvariantCultureIgnoreCase) ?? false);
                discAsPkg |= items["serial"]?.StartsWith("NP", StringComparison.InvariantCultureIgnoreCase) ?? false;
                discAsPkg |= items["ldr_game_serial"] is string ldrGameSerial && ldrGameSerial.StartsWith("NP", StringComparison.InvariantCultureIgnoreCase);
            }

            discAsPkg |= items["game_category"] == "HG" && !(items["serial"]?.StartsWith("NP", StringComparison.InvariantCultureIgnoreCase) ?? false);
            if (discInsideGame)
                notes.Add($"❌ Disc game inside `{items["ldr_disc"]}`");
            DiscordEmoji pirateEmoji = null;
            if (discAsPkg)
            {
                pirateEmoji = discordClient.GetEmoji(":piratethink:", DiscordEmoji.FromUnicode("🔨"));
                notes.Add($"{pirateEmoji} Disc game installed as a PKG ");
            }

            if (!string.IsNullOrEmpty(items["native_ui_input"]))
                notes.Add("⚠ Pad initialization problem detected; try disabling `Native UI`");
            if (!string.IsNullOrEmpty(items["xaudio_init_error"]))
                notes.Add("❌ XAudio initialization failed; make sure you have audio output device working");

            if (!string.IsNullOrEmpty(items["fw_missing_msg"])
                || !string.IsNullOrEmpty(items["fw_missing_something"]))
                notes.Add("❌ PS3 firmware is missing or corrupted");

            var updateInfo = await CheckForUpdateAsync(items).ConfigureAwait(false);
            var buildBranch = items["build_branch"]?.ToLowerInvariant();
            if (updateInfo != null && (buildBranch == "head" || buildBranch == "spu_perf"))
            {
                string prefix = "⚠";
                string timeDeltaStr;
                if (updateInfo.GetUpdateDelta() is TimeSpan timeDelta)
                {
                    timeDeltaStr = timeDelta.AsTimeDeltaDescription() + " old";
                    if (timeDelta > PrehistoricBuild)
                        prefix = "😱";
                    else if (timeDelta > AncientBuild)
                        prefix = "💢";
                    //else if (timeDelta > VeryVeryOldBuild)
                    //    prefix = "💢";
                    else if (timeDelta > VeryOldBuild)
                        prefix = "‼";
                    else if (timeDelta > OldBuild)
                        prefix = "❗";
                }
                else
                    timeDeltaStr = "outdated";

                notes.Add($"{prefix} This RPCS3 build is {timeDeltaStr}, please consider updating it");
                if (buildBranch == "spu_perf")
                    notes.Add($"ℹ `{buildBranch}` build is obsolete, current master build offers at least the same level of performance and includes many additional improvements");
            }

            if (state.Error == LogParseState.ErrorCode.SizeLimit)
                notes.Add("ℹ The log was too large, so only the last processed run is shown");

            var notesContent = new StringBuilder();
            foreach (var line in SortLines(notes, pirateEmoji))
                notesContent.AppendLine(line);
            PageSection(builder, notesContent.ToString().Trim(), "Notes");
        }

        private static void BuildMissingLicensesSection(DiscordEmbedBuilder builder, NameValueCollection items)
        {
            if (items["rap_file"] is string rap)
            {
                var limitTo = 5;
                var licenseNames = rap.Split(Environment.NewLine)
                    .Distinct()
                    .Select(Path.GetFileName)
                    .Distinct()
                    .Except(KnownBogusLicenses)
                    .Select(p => $"`{p}`")
                    .ToList();
                if (licenseNames.Count == 0)
                    return;

                string content;
                if (licenseNames.Count > limitTo)
                {
                    content = string.Join(Environment.NewLine, licenseNames.Take(limitTo - 1));
                    var other = licenseNames.Count - limitTo + 1;
                    content += $"{Environment.NewLine}and {other} other license{StringUtils.GetSuffix(other)}";
                }
                else
                    content = string.Join(Environment.NewLine, licenseNames);

                builder.AddField("Missing Licenses", content);
            }
        }

        private static async Task<(bool irdChecked, bool broken)> HasBrokenFilesAsync(NameValueCollection items)
        {
            if (!(items["serial"] is string productCode))
                return (false, false);

            if (!productCode.StartsWith("B") && !productCode.StartsWith("M"))
                return (false, false);

            if (string.IsNullOrEmpty(items["broken_directory"])
                && string.IsNullOrEmpty(items["broken_filename"]))
                return (false, false);

            var getIrdTask = irdClient.DownloadAsync(productCode, Config.IrdCachePath, Config.Cts.Token);
            var missingDirs = items["broken_directory"]?.Split(Environment.NewLine).Distinct().ToList() ??
                              new List<string>(0);
            var missingFiles = items["broken_filename"]?.Split(Environment.NewLine).Distinct().ToList() ??
                               new List<string>(0);
            HashSet<string> knownFiles;
            try
            {
                var irdFiles = await getIrdTask.ConfigureAwait(false);
                knownFiles = new HashSet<string>(
                    from ird in irdFiles
                    from name in ird.GetFilenames()
                    select name,
                    StringComparer.InvariantCultureIgnoreCase
                );
            }
            catch (Exception e)
            {
                Config.Log.Warn(e, "Failed to get IRD files for " + productCode);
                return (false, false);
            }

            if (knownFiles.Count == 0)
                return (false, false);

            var broken = missingFiles.Any(knownFiles.Contains);
            if (broken)
                return (true, true);

            var knownDirs = new HashSet<string>(knownFiles.Select(f => Path.GetDirectoryName(f).Replace('\\', '/')),
                StringComparer.InvariantCultureIgnoreCase);
            return (true, missingDirs.Any(knownDirs.Contains));
        }
    }
}