﻿using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CompatApiClient.Utils;
using CompatBot.Commands;
using CompatBot.EventHandlers.LogParsing;
using CompatBot.EventHandlers.LogParsing.POCOs;
using CompatBot.EventHandlers.LogParsing.SourceHandlers;
using CompatBot.Utils;
using CompatBot.Utils.ResultFormatters;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.EventArgs;

namespace CompatBot.EventHandlers
{
    internal static class LogParsingHandler
    {
        private static readonly char[] linkSeparator = { ' ', '>', '\r', '\n' };
        private static readonly ISourceHandler[] handlers =
        {
            new GzipHandler(),
            new PlainTextHandler(),
            new ZipHandler(),
            new RarHandler(),
            new SevenZipHandler(),
        };

        private static readonly SemaphoreSlim QueueLimiter = new SemaphoreSlim(Math.Max(1, Environment.ProcessorCount / 2), Math.Max(1, Environment.ProcessorCount / 2));
        private delegate void OnLog(DiscordClient client, DiscordChannel channel, DiscordMessage message, DiscordMember requester = null);
        private static event OnLog OnNewLog;

        static LogParsingHandler()
        {
            OnNewLog += EnqueueLogProcessing;
        }

        public static Task OnMessageCreated(MessageCreateEventArgs args)
        {
            if (DefaultHandlerFilter.IsFluff(args.Message))
                return Task.CompletedTask;

            OnNewLog(args.Client, args.Channel, args.Message);
            return Task.CompletedTask;
        }

        public static async void EnqueueLogProcessing(DiscordClient client, DiscordChannel channel, DiscordMessage message, DiscordMember requester = null)
        {
            try
            {
                if (!QueueLimiter.Wait(0))
                {
                    await channel.SendMessageAsync("Log processing is rate limited, try again a bit later").ConfigureAwait(false);
                    return;
                }

                bool parsedLog = false;
                var startTime = Stopwatch.StartNew();
                DiscordMessage botMsg = null;
                try
                {
                    foreach (var attachment in message.Attachments.Where(a => !a.FileName.EndsWith("tty.log", StringComparison.InvariantCultureIgnoreCase)))
                    foreach (var handler in handlers)
                        if (await handler.CanHandleAsync(attachment).ConfigureAwait(false))
                        {
                            Config.Log.Debug($">>>>>>> {message.Id % 100} Parsing log from attachment {attachment.FileName} ({attachment.FileSize})...");
                            botMsg = await channel.SendMessageAsync(embed: GetAnalyzingMsgEmbed().AddAuthor(client, message)).ConfigureAwait(false);
                            parsedLog = true;
                            LogParseState result = null;
                            try
                            {
                                var pipe = new Pipe();
                                var fillPipeTask = handler.FillPipeAsync(attachment, pipe.Writer);
                                result = await LogParser.ReadPipeAsync(pipe.Reader).ConfigureAwait(false);
                                await fillPipeTask.ConfigureAwait(false);
                            }
                            catch (Exception e)
                            {
                                Config.Log.Error(e, "Log parsing failed");
                            }

                            if (result == null)
                            {
                                botMsg = await botMsg.UpdateOrCreateMessageAsync(channel,
                                    embed: new DiscordEmbedBuilder
                                        {
                                            Description = "Log analysis failed, most likely cause is a truncated/invalid log.\n" +
                                                          "Please run the game again and re-upload a new copy.",
                                            Color = Config.Colors.LogResultFailed,
                                        }
                                        .AddAuthor(client, message)
                                        .Build()
                                ).ConfigureAwait(false);
                            }
                            else
                            {
                                try
                                {
                                    if (result.Error == LogParseState.ErrorCode.PiracyDetected)
                                    {
                                        if (message.Author.IsWhitelisted(client, channel.Guild))
                                        {
                                            var piracyWarning = await result.AsEmbedAsync(client, message).ConfigureAwait(false);
                                            piracyWarning = piracyWarning.WithDescription("Please remove the log and issue warning to the original author of the log");
                                            botMsg = await botMsg.UpdateOrCreateMessageAsync(channel, embed: piracyWarning).ConfigureAwait(false);
                                            await client.ReportAsync("Pirated Release (whitelisted by role)", message, result.PiracyTrigger, result.PiracyContext, ReportSeverity.Low).ConfigureAwait(false);
                                        }
                                        else
                                        {
                                            var severity = ReportSeverity.Low;
                                            try
                                            {
                                                await message.DeleteAsync("Piracy detected in log").ConfigureAwait(false);
                                            }
                                            catch (Exception e)
                                            {
                                                severity = ReportSeverity.High;
                                                Config.Log.Warn(e, $"Unable to delete message in {channel.Name}");
                                            }
                                            try
                                            {
                                                botMsg = await botMsg.UpdateOrCreateMessageAsync(channel,
                                                    $"{message.Author.Mention}, please read carefully:",
                                                    embed: await result.AsEmbedAsync(client, message).ConfigureAwait(false)
                                                ).ConfigureAwait(false);
                                            }
                                            catch (Exception e)
                                            {
                                                Config.Log.Error(e, "Failed to send piracy warning");
                                            }
                                            await Task.WhenAll(
                                                client.ReportAsync("Pirated Release", message, result.PiracyTrigger, result.PiracyContext, severity),
                                                Warnings.AddAsync(client, message, message.Author.Id, message.Author.Username, client.CurrentUser, "Pirated Release", $"{result.PiracyTrigger} - {result.PiracyContext.Sanitize()}")
                                            );
                                        }
                                    }
                                    else
                                        botMsg = await botMsg.UpdateOrCreateMessageAsync(channel,
                                            requester == null ? null : $"Analyzed log from {client.GetMember(channel.Guild, message.Author)?.GetUsernameWithNickname()} by request from {requester.Mention}:",
                                            embed: await result.AsEmbedAsync(client, message).ConfigureAwait(false)
                                        ).ConfigureAwait(false);
                                }
                                catch (Exception e)
                                {
                                    Config.Log.Error(e, "Sending log results failed");
                                }
                            }
                            return;
                        }

                    if (!"help".Equals(channel.Name, StringComparison.InvariantCultureIgnoreCase))
                        return;

                    var potentialLogExtension = message.Attachments.Select(a => Path.GetExtension(a.FileName).ToUpperInvariant().TrimStart('.')).FirstOrDefault();
                    switch (potentialLogExtension)
                    {
                        case "TXT":
                        {
                            await channel.SendMessageAsync($"{message.Author.Mention} Please upload the full RPCS3.log.gz (or RPCS3.log with a zip/rar icon) file after closing the emulator instead of copying the logs from RPCS3's interface, as it doesn't contain all the required information.").ConfigureAwait(false);
                            return;
                        }
                    }

                    if (string.IsNullOrEmpty(message.Content))
                        return;

                    var linkStart = message.Content.IndexOf("http");
                    if (linkStart > -1)
                    {
                        var link = message.Content.Substring(linkStart).Split(linkSeparator, 2)[0];
                        if (link.Contains(".log", StringComparison.InvariantCultureIgnoreCase) || link.Contains("rpcs3.zip", StringComparison.CurrentCultureIgnoreCase))
                            await channel.SendMessageAsync("If you intended to upload a log file please re-upload it directly to discord").ConfigureAwait(false);
                    }
                }
                finally
                {
                    QueueLimiter.Release();
                    if (parsedLog)
                        Config.Log.Debug($"<<<<<<< {message.Id % 100} Finished parsing in {startTime.Elapsed}");
                }
            }
            catch (Exception e)
            {
                Config.Log.Error(e, "Error parsing log");
            }
        }

        private static DiscordEmbedBuilder GetAnalyzingMsgEmbed()
        {
            return new DiscordEmbedBuilder
            {
                Description = "👀 Looking at the log, please wait... 👀",
                Color = Config.Colors.LogUnknown,
            };
        }
    }
}
