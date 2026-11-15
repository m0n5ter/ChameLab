using System.Text.Json.Serialization;
using ChameLab.Models;
using ChameLab.Protocol;

namespace ChameLab.Cli;

/// <summary>Source-generated JSON for every type the commands read or print, so trimmed builds (iOS, browser) keep working.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(CommandSpec[]))]
[JsonSerializable(typeof(Dictionary<string, Dictionary<string, int>>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(LfSniffSummary))]
[JsonSerializable(typeof(LfModulationResult))]
[JsonSerializable(typeof(Mf1EmulatorConfig))]
[JsonSerializable(typeof(Hf14aTag))]
internal partial class CliJsonContext : JsonSerializerContext;
