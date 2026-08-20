using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace BehaviorDiff.Mcp;

/// <summary>
/// Read-only queries over artifacts the engine has already produced. Nothing here traces, diffs, or
/// interprets: if a question needs data the engine does not emit, the answer says so.
/// </summary>
[McpServerToolType]
public static class BehaviorDiffTools
{
    private const int MemberCap = 50;
    private const int ObservationCap = 5;

    [McpServerTool(Name = "run_analysis")]
    [Description("""
        Starts a BehaviorDiff analysis of a pull request and returns immediately with a run_id.
        It does NOT wait for the analysis; poll get_run_status until status is 'complete' or 'refused'.

        BehaviorDiff compares the RUNTIME BEHAVIOR of two builds by running the repository's own test
        suite against both and recording what every instrumented method was called with and returned.
        This is not a source diff. Its purpose is to find behavior changes the source diff cannot show
        you, particularly in files the pull request did not modify.

        repo_path: absolute path to a git working tree.
        base_ref:  git ref for the base (usually the target branch's merge base).
        pr_ref:    git ref for the pull request head.
        """)]
    public static string RunAnalysis(string repoPath, string baseRef, string prRef)
    {
        if (!Directory.Exists(repoPath))
        {
            return Fail("repo_path does not exist: " + repoPath);
        }

        RunRecord record = RunStore.Create(repoPath, baseRef, prRef);
        AnalysisRunner.StartInBackground(record);

        return JsonSerializer.Serialize(new { run_id = record.RunId, status = record.Status });
    }

    [McpServerTool(Name = "get_run_status")]
    [Description("""
        Returns the state of an analysis started by run_analysis.

        status is one of:
          queued    - accepted, not started
          running   - in progress; phase and progress describe where it is
          complete  - finished; results are available from list_divergences
          refused   - the engine declined to report a result because it could not justify one.
                      This is NOT a clean result. error explains why. Do not tell the user the pull
                      request is safe after a refusal.
          failed    - the analysis could not run (build failure, missing refs). error explains.
        """)]
    public static string GetRunStatus(string runId)
    {
        RunRecord? record = RunStore.Load(runId);
        if (record is null)
        {
            return Fail("unknown run_id: " + runId);
        }

        return JsonSerializer.Serialize(new
        {
            status = record.Status,
            phase = record.Phase,
            progress = record.Progress,
            error = record.Error,
            exit_code = record.ExitCode,
        });
    }

    [McpServerTool(Name = "list_divergences")]
    [Description("""
        Lists behavior changes found by a completed analysis, rolled up by member.

        category:
          unexpected (default) - a behavior change in a file the pull request did NOT modify.
                                 THIS IS THE POINT OF THE TOOL. A change here means editing one file
                                 altered behavior somewhere else, which a source diff cannot show.
                                 Report these first and most prominently.
          expected             - a behavior change in a file the pull request did modify. Usually the
                                 intended effect of the change; useful for confirming coverage.
          all                  - both.

        Returns summaries only: member name, file, how many call sites, how many distinct tests, and
        the symptom kinds. It does NOT return argument or return values; use get_divergence for those.

        Capped at 50 members. If the result is truncated it says so explicitly.
        """)]
    public static string ListDivergences(string runId, string category = "unexpected", int? limit = null)
    {
        if (Guard(runId, out string? guard, out JsonDocument? findings) is false)
        {
            return guard!;
        }

        string wanted = category.ToLowerInvariant();
        var members = Members(findings!)
            .Where(member => wanted == "all" || Str(member, "attribution").Equals(wanted, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(member => Int(member, "callSiteCount"))
            .ToList();

        int cap = Math.Min(limit ?? MemberCap, MemberCap);
        bool truncated = members.Count > cap;

        return JsonSerializer.Serialize(new
        {
            category = wanted,
            total_members = members.Count,
            total_call_sites = members.Sum(member => Int(member, "callSiteCount")),
            returned = Math.Min(cap, members.Count),
            truncated,
            truncation_note = truncated
                ? $"Showing {cap} of {members.Count} members, ordered by call-site count. Narrow by category or raise limit (max {MemberCap})."
                : null,
            members = members.Take(cap).Select(member => new
            {
                member = Str(member, "memberName"),
                attribution = Str(member, "attribution"),
                file = NullableStr(member, "filePath"),
                line = NullableInt(member, "line"),
                source_generated = Bool(member, "sourceGenerated"),
                call_sites = Int(member, "callSiteCount"),
                distinct_tests = Int(member, "distinctTestCount"),
                verified = Bool(member, "verified"),
                symptoms = Strings(member, "symptoms").Take(4).ToArray(),
            }),
        });
    }

    [McpServerTool(Name = "get_divergence")]
    [Description("""
        Full detail for one member reported by list_divergences.

        Returns the member's source file and line, up to 5 observations showing the base and PR rendered
        values side by side, how many descendant calls were compared, and any downgrade reason.

        A downgrade reason means the finding could not be fully verified and says why - for example the
        member exists on only one side, or its subtree calls something that was never instrumented.
        Report the reason alongside the finding; do not present a downgraded finding as confirmed.
        """)]
    public static string GetDivergence(string runId, string memberName)
    {
        if (Guard(runId, out string? guard, out JsonDocument? findings) is false)
        {
            return guard!;
        }

        JsonElement? matching = Members(findings!).FirstOrDefault(member => Str(member, "memberName") == memberName);
        if (matching is null || matching.Value.ValueKind == JsonValueKind.Undefined)
        {
            return Fail("no member '" + memberName + "' in run " + runId + "; call list_divergences first");
        }

        JsonElement member = matching.Value;
        JsonElement[] observations = member.TryGetProperty("evidence", out JsonElement evidence)
            ? evidence.EnumerateArray().Take(ObservationCap).ToArray()
            : Array.Empty<JsonElement>();
        return JsonSerializer.Serialize(new
        {
            member = memberName,
            attribution = Str(member, "attribution"),
            file = NullableStr(member, "filePath"),
            line = NullableInt(member, "line"),
            source_generated = Bool(member, "sourceGenerated"),
            source_generated_note = NullableStr(member, "sourceGeneratedNote"),
            call_sites = Int(member, "callSiteCount"),
            descendants_compared = Int(member, "descendantsCompared"),
            verified = Bool(member, "verified"),
            downgrade_reasons = Strings(member, "downgradeReasons").ToArray(),
            observations,
            observations_note = observations.Length == 0
                ? "The engine records no per-observation values for this kind of finding; a member that exists on only one side has no counterpart to compare."
                : null,
            git_diff_hunks = "not available: the engine does not emit diff hunks. Read the file at the path above.",
        });
    }

    [McpServerTool(Name = "get_call_path")]
    [Description("""
        The ordered chain of calls from the test that reached this member, outermost first.
        Use it to explain how a change in one file was reached from a test that appears unrelated.
        """)]
    public static string GetCallPath(string runId, string memberName)
    {
        if (Guard(runId, out string? guard, out JsonDocument? _) is false)
        {
            return guard!;
        }

        JsonDocument? set = RunStore.LoadArtifact(runId, "divergence-set.json");
        if (set is null || !set.RootElement.TryGetProperty("callTree", out JsonElement tree))
        {
            return Fail("run " + runId + " has no call tree; the divergence set was not emitted");
        }

        var byId = new Dictionary<long, JsonElement>();
        foreach (JsonElement node in tree.EnumerateArray())
        {
            if (TryId(node, "callId", out long value))
            {
                byId[value] = node;
            }
        }

        JsonElement target = default;
        bool found = false;
        foreach (JsonElement node in tree.EnumerateArray())
        {
            if (Str(node, "methodFullName") == memberName)
            {
                target = node;
                found = true;
                break;
            }
        }

        if (!found)
        {
            return Fail("no call to '" + memberName + "' in run " + runId);
        }

        var chain = new List<object>();
        JsonElement current = target;
        var seen = new HashSet<long>();
        while (true)
        {
            chain.Add(new { member = Str(current, "methodFullName"), file = Str(current, "filePath") });
            if (!TryId(current, "parentCallId", out long parentId)
                || !seen.Add(parentId)
                || !byId.TryGetValue(parentId, out JsonElement next))
            {
                break;
            }

            current = next;
        }

        chain.Reverse();
        return JsonSerializer.Serialize(new { member = memberName, test = Str(target, "testId"), path = chain });
    }

    [McpServerTool(Name = "get_untested_divergences")]
    [Description("""
        Members whose behavior changed with no test assertion reacting to the change.

        This is an approximation: a test whose own trace is identical is treated as not having observed
        the change. It does not prove the value is unasserted, only that nothing reacted in this run.
        These are the changes most likely to reach production unnoticed.
        """)]
    public static string GetUntestedDivergences(string runId)
    {
        if (Guard(runId, out string? guard, out JsonDocument? findings) is false)
        {
            return guard!;
        }

        var untested = Members(findings!)
            .Where(member => Int(member, "untestedCallSiteCount") > 0)
            .Select(member => new
            {
                member = Str(member, "memberName"),
                attribution = Str(member, "attribution"),
                file = NullableStr(member, "filePath"),
                call_sites = Int(member, "untestedCallSiteCount"),
            })
            .OrderByDescending(m => m.call_sites)
            .Take(MemberCap)
            .ToList();

        return JsonSerializer.Serialize(new
        {
            total_members = untested.Count,
            approximation_note = "A test whose own trace is identical is treated as not having observed the change. This does not prove the value is unasserted.",
            members = untested,
        });
    }

    private static bool Guard(string runId, out string? failure, out JsonDocument? findings)
    {
        findings = null;
        RunRecord? record = RunStore.Load(runId);
        if (record is null)
        {
            failure = Fail("unknown run_id: " + runId);
            return false;
        }

        findings = RunStore.LoadArtifact(runId, "findings.json");
        if (findings is null)
        {
            failure = Fail("run " + runId + " has no findings.json. This is not a clean result and must not be reported as one.");
            return false;
        }

        string status = Str(findings.RootElement, "status");
        if (status is "refused" or "failed")
        {
            string reason = findings.RootElement.TryGetProperty("refusal", out JsonElement refusal)
                ? Str(refusal, "reason")
                : "no reason recorded";
            failure = Fail("could not analyze this pull request (" + status + "): " + reason
                + ". This is not a clean result and must not be reported as one.");
            return false;
        }

        if (status != "analyzed")
        {
            failure = Fail("run " + runId + " has unknown findings status '" + status + "'");
            return false;
        }

        failure = null;
        return true;
    }

    private static IEnumerable<JsonElement> Members(JsonDocument findings) =>
        findings.RootElement.TryGetProperty("members", out JsonElement members) && members.ValueKind == JsonValueKind.Array
            ? members.EnumerateArray()
            : Enumerable.Empty<JsonElement>();

    private static IEnumerable<string> Strings(JsonElement node, string name) =>
        node.TryGetProperty(name, out JsonElement a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Select(e => e.GetString() ?? string.Empty)
            : Enumerable.Empty<string>();

    private static string Str(JsonElement node, string name) =>
        node.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;

    private static string? NullableStr(JsonElement node, string name) =>
        node.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int Int(JsonElement node, string name) =>
        node.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i)
            ? i
            : 0;

    private static int? NullableInt(JsonElement node, string name) =>
        node.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out int number)
            ? number
            : null;

    private static bool Bool(JsonElement node, string name) =>
        node.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    /// <summary>
    /// Call ids are written as numbers by some engine versions and as strings by others.
    /// TryGetInt64 throws rather than returning false on a mismatched kind, so both are handled here.
    /// </summary>
    private static bool TryId(JsonElement node, string name, out long id)
    {
        id = 0;
        if (!node.TryGetProperty(name, out JsonElement v))
        {
            return false;
        }

        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt64(out id),
            JsonValueKind.String => long.TryParse(v.GetString(), out id),
            _ => false,
        };
    }

    private static string Fail(string reason) =>
        JsonSerializer.Serialize(new { error = reason, is_clean_result = false });
}
