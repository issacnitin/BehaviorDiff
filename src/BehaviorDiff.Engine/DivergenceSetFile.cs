using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BehaviorDiff.Engine
{
    /// <summary>Shape of the part 1 artifact. Part 2 reads it and never re-derives matching.</summary>
    internal sealed class DivergenceSetFile
    {
        [JsonPropertyName("schema")] public string Schema { get; set; } = string.Empty;

        [JsonPropertyName("counts")] public CountsDto Counts { get; set; } = new();

        [JsonPropertyName("matchedKeys")] public List<MatchedKeyDto> MatchedKeys { get; set; } = new();

        [JsonPropertyName("divergences")] public List<DivergenceDto> Divergences { get; set; } = new();

        [JsonPropertyName("harnessDivergences")] public List<HarnessDivergenceDto> HarnessDivergences { get; set; } = new();

        [JsonPropertyName("toolingGaps")] public List<GapDto> ToolingGaps { get; set; } = new();

        [JsonPropertyName("manifestNoise")] public List<GapDto> ManifestNoise { get; set; } = new();

        [JsonPropertyName("coverage")] public CoverageDto Coverage { get; set; } = new();

        [JsonPropertyName("callTree")] public List<CallNodeDto> CallTree { get; set; } = new();

        [JsonPropertyName("prCallTree")] public List<CallNodeDto> PrCallTree { get; set; } = new();
    }

    internal sealed class CountsDto
    {
        [JsonPropertyName("matchedKeys")] public int MatchedKeys { get; set; }

        [JsonPropertyName("rawDifferences")] public int RawDifferences { get; set; }

        [JsonPropertyName("remainingDivergences")] public int RemainingDivergences { get; set; }
    }

    internal sealed class MatchedKeyDto
    {
        [JsonPropertyName("testId")] public string TestId { get; set; } = string.Empty;

        [JsonPropertyName("methodFullName")] public string MethodFullName { get; set; } = string.Empty;

        [JsonPropertyName("filePath")] public string? FilePath { get; set; }

        [JsonPropertyName("baseCalls")] public int BaseCalls { get; set; }

        [JsonPropertyName("prCalls")] public int PrCalls { get; set; }

        [JsonPropertyName("digestConfidence")] public string DigestConfidence { get; set; } = "Exact";

        [JsonPropertyName("partialMarkers")] public List<string> PartialMarkers { get; set; } = new();
    }

    internal sealed class DivergenceDto
    {
        [JsonPropertyName("testId")] public string TestId { get; set; } = string.Empty;

        [JsonPropertyName("methodFullName")] public string MethodFullName { get; set; } = string.Empty;

        [JsonPropertyName("filePath")] public string? FilePath { get; set; }

        [JsonPropertyName("ordinal")] public int? Ordinal { get; set; }

        [JsonPropertyName("kind")] public string Kind { get; set; } = string.Empty;

        [JsonPropertyName("detail")] public string Detail { get; set; } = string.Empty;

        [JsonPropertyName("digestConfidence")] public string DigestConfidence { get; set; } = "Exact";

        [JsonPropertyName("baseReturnRendered")] public string? BaseReturnRendered { get; set; }

        [JsonPropertyName("prReturnRendered")] public string? PrReturnRendered { get; set; }

        [JsonPropertyName("baseArgsRendered")] public string? BaseArgsRendered { get; set; }

        [JsonPropertyName("prArgsRendered")] public string? PrArgsRendered { get; set; }
    }

    internal sealed class HarnessDivergenceDto
    {
        [JsonPropertyName("testId")] public string TestId { get; set; } = string.Empty;

        [JsonPropertyName("methodFullName")] public string MethodFullName { get; set; } = string.Empty;

        [JsonPropertyName("kind")] public string Kind { get; set; } = string.Empty;

        // Null means an older artifact that predates root classification; treat it conservatively.
        [JsonPropertyName("isTestRoot")] public bool? IsTestRoot { get; set; }
    }

    internal sealed class GapDto
    {
        [JsonPropertyName("scope")] public string Scope { get; set; } = string.Empty;

        [JsonPropertyName("assembly")] public string Assembly { get; set; } = string.Empty;

        [JsonPropertyName("methodFullName")] public string? MethodFullName { get; set; }
    }

    internal sealed class CoverageDto
    {
        [JsonPropertyName("members")] public List<CoverageMemberDto> Members { get; set; } = new();

        [JsonPropertyName("assemblies")] public List<CoverageAssemblyDto> Assemblies { get; set; } = new();
    }

    internal sealed class CoverageMemberDto
    {
        [JsonPropertyName("methodFullName")] public string? MethodFullName { get; set; }

        [JsonPropertyName("assembly")] public string Assembly { get; set; } = string.Empty;

        [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;

        [JsonPropertyName("skipReason")] public string? SkipReason { get; set; }

        [JsonPropertyName("sourceResolution")] public string? SourceResolution { get; set; }

        [JsonPropertyName("isTestRoot")] public bool IsTestRoot { get; set; }
    }

    internal sealed class CoverageAssemblyDto
    {
        [JsonPropertyName("assembly")] public string Assembly { get; set; } = string.Empty;


        [JsonPropertyName("sourcePartial")] public bool SourcePartial { get; set; }
    }

    internal sealed class CallNodeDto
    {
        [JsonPropertyName("callId")] public long CallId { get; set; }

        [JsonPropertyName("parentCallId")] public long? ParentCallId { get; set; }

        [JsonPropertyName("testId")] public string TestId { get; set; } = string.Empty;

        [JsonPropertyName("methodFullName")] public string MethodFullName { get; set; } = string.Empty;

        [JsonPropertyName("ordinal")] public int? Ordinal { get; set; }

        [JsonPropertyName("isHarness")] public bool IsHarness { get; set; }

        [JsonPropertyName("filePath")] public string? FilePath { get; set; }

        [JsonPropertyName("line")] public int? Line { get; set; }

        [JsonPropertyName("process")] public string Process { get; set; } = string.Empty;
    }

    internal static class DivergenceSetReader
    {
        internal static DivergenceSetFile Read(string path)
        {
            if (!File.Exists(path))
            {
                throw new DiffInputException("DivergenceSet not found: " + path);
            }

            DivergenceSetFile? file = JsonSerializer.Deserialize<DivergenceSetFile>(File.ReadAllText(path));
            if (file is null)
            {
                throw new DiffInputException("DivergenceSet is empty: " + path);
            }

            if (!string.Equals(file.Schema, "behaviordiff.divergenceset/2", StringComparison.Ordinal))
            {
                throw new DiffInputException("Unexpected schema '" + file.Schema + "' in " + path);
            }

            return file;
        }
    }
}
