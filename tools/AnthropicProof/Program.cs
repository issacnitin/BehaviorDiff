using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RealDiff.Cli;

if (args.Length != 1 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("usage: RealDiff.AnthropicProof <findings.json>");
    return 2;
}

using JsonDocument findings = JsonDocument.Parse(File.ReadAllText(args[0]));
JsonElement member = findings.RootElement.GetProperty("members").EnumerateArray()
    .First(item => item.GetProperty("attribution").GetString() == "unexpected");
string effectPath = member.GetProperty("filePath").GetString()
    ?? throw new InvalidOperationException("unexpected member has no source path");
int effectLine = member.GetProperty("line").GetInt32();
var patches = new[]
{
    new ChangedFilePatch(
        "samples/SampleApp/SettingsParser.cs",
        "@@ -10,7 +10,7 @@ namespace SampleApp\n-private const decimal DefaultFreeShippingThreshold = 50m;\n+private const decimal DefaultFreeShippingThreshold = 30m;"),
};

const string observationCitation = "OBSERVATION: test=SampleApp.Tests.ShippingTests.Order_below_threshold_pays_shipping; baseArgs=orderTotal=Primitive:40; prArgs=orderTotal=Primitive:40; baseReturn=Primitive:false; prReturn=Primitive:true; baseException=; prException=";
const string diffCitation = "DIFF: samples/SampleApp/SettingsParser.cs: +private const decimal DefaultFreeShippingThreshold = 30m;";
var whyCitations = new List<string> { observationCitation, diffCitation };
if (member.TryGetProperty("consequences", out JsonElement consequences)
    && consequences.ValueKind == JsonValueKind.Array
    && consequences.GetArrayLength() > 0)
{
    JsonElement consequence = consequences[0];
    JsonElement consequenceEvidence = consequence.GetProperty("evidence");
    whyCitations.Add("CONSEQUENCE: test=" + consequenceEvidence.GetProperty("testId").GetString()
        + "; member=" + consequence.GetProperty("memberName").GetString()
        + "; baseReturn=" + consequenceEvidence.GetProperty("baseReturn").GetString()
        + "; prReturn=" + consequenceEvidence.GetProperty("prReturn").GetString()
        + "; baseException=" + (consequenceEvidence.TryGetProperty("baseException", out JsonElement baseException)
            ? baseException.GetString()
            : string.Empty)
        + "; prException=" + (consequenceEvidence.TryGetProperty("prException", out JsonElement prException)
            ? prException.GetString()
            : string.Empty));
}

string acceptedText = JsonSerializer.Serialize(new
{
    why = new
    {
        text = "The PR changes DefaultFreeShippingThreshold, which is consumed by the unedited IsFreeShipping predicate.",
        citations = whyCitations,
    },
    test = new
    {
        text = "Add a test that applies the default settings, calls IsFreeShipping with 40, and expects false; it passes on base and fails on the PR because the result is true.",
        citations = new[] { observationCitation },
    },
});
var acceptedHandler = new FakeHandler(Response(acceptedText));
ModelExplanation? accepted;
using (var explainer = new AnthropicExplainer(
    "proof-key",
    acceptedHandler,
    "https://example.invalid/v1/messages",
    "claude-sonnet-5"))
{
    accepted = await explainer.ExplainAsync(member, patches);
}

Assert(accepted is not null, "grounded response was rejected");
Assert(acceptedHandler.RequestCount == 1, "expected exactly one request for one member");
Assert(acceptedHandler.LastRequestBody.Contains("DefaultFreeShippingThreshold", StringComparison.Ordinal), "diff identifier missing from prompt");
Assert(acceptedHandler.LastRequestBody.Contains("baseCallPath", StringComparison.Ordinal), "call path missing from prompt");
Assert(acceptedHandler.LastRequestBody.Contains("assertionReaction", StringComparison.Ordinal), "untested evidence missing from prompt");
Assert(acceptedHandler.LastRequestBody.Contains("CONSEQUENCE:", StringComparison.Ordinal), "downstream consequence missing from prompt");
Assert(acceptedHandler.LastRequestBody.Contains("do not restate them", StringComparison.Ordinal), "prompt does not prohibit evidence restatement");
Assert(acceptedHandler.LastRequestBody.Contains("do not mention returns", StringComparison.Ordinal), "prompt does not prohibit outcome restatement");
Assert(acceptedHandler.SawApiKey && acceptedHandler.SawVersion, "required Anthropic headers missing");

JsonElement relatedObservation = member.GetProperty("evidence").EnumerateArray().First();
string relatedCitation = "RELATED: member=" + member.GetProperty("memberName").GetString()
    + "; test=" + relatedObservation.GetProperty("testId").GetString()
    + "; baseArgs=" + relatedObservation.GetProperty("baseArgs").GetString()
    + "; prArgs=" + relatedObservation.GetProperty("prArgs").GetString()
    + "; baseReturn=" + relatedObservation.GetProperty("baseReturn").GetString()
    + "; prReturn=" + relatedObservation.GetProperty("prReturn").GetString();
string relatedAcceptedText = JsonSerializer.Serialize(new
{
    why = new
    {
        text = "The PR changes DefaultFreeShippingThreshold, which is consumed by the unedited IsFreeShipping predicate.",
        citations = whyCitations.Append(relatedCitation).ToArray(),
    },
    test = new
    {
        text = "Add a test that applies the default settings, calls IsFreeShipping with 40, and expects false; it passes on base and fails on the PR because the result is true.",
        citations = new[] { observationCitation },
    },
});
var relatedAcceptedHandler = new FakeHandler(Response(relatedAcceptedText));
using (var explainer = new AnthropicExplainer(
    "proof-key",
    relatedAcceptedHandler,
    "https://example.invalid/v1/messages",
    "claude-sonnet-5"))
{
    ModelExplanation? relatedAccepted = await explainer.ExplainAsync(member, patches, new[] { member });
    Assert(relatedAccepted is not null, "response with an exact related-frontier citation was rejected");
    Assert(relatedAcceptedHandler.LastRequestBody.Contains("relatedFrontiers", StringComparison.Ordinal),
        "related frontier evidence was omitted from the prompt");
}

var missingRelatedHandler = new FakeHandler(Response(acceptedText));
using (var explainer = new AnthropicExplainer(
    "proof-key",
    missingRelatedHandler,
    "https://example.invalid/v1/messages",
    "claude-sonnet-5"))
{
    ExplanationAttempt missingRelated = await explainer.ExplainWithDiagnosticsAsync(member, patches, new[] { member });
    Assert(missingRelated.Explanation is null, "response without a required related-frontier citation was accepted");
}

string rejectedText = JsonSerializer.Serialize(new
{
    why = new
    {
        text = "The behavior changed for an unknown reason.",
        citations = new[]
        {
            "OBSERVATION: test=SampleApp.Tests.ShippingTests.Order_below_threshold_pays_shipping; baseArgs=orderTotal=Primitive:40; prArgs=orderTotal=Primitive:40; baseReturn=Primitive:false; prReturn=Primitive:true; baseException=; prException=",
            "DIFF: samples/SampleApp/SettingsParser.cs: +private const decimal DefaultFreeShippingThreshold = 30m;",
        },
    },
    test = new
    {
        text = "Add a regression test.",
        citations = new[]
        {
            "OBSERVATION: test=SampleApp.Tests.ShippingTests.Order_below_threshold_pays_shipping; baseArgs=orderTotal=Primitive:40; prArgs=orderTotal=Primitive:40; baseReturn=Primitive:false; prReturn=Primitive:true; baseException=; prException=",
        },
    },
});
var rejectedHandler = new FakeHandler(Response(rejectedText));
using (var explainer = new AnthropicExplainer(
    "proof-key",
    rejectedHandler,
    "https://example.invalid/v1/messages",
    "claude-sonnet-5"))
{
    ExplanationAttempt rejected = await explainer.ExplainWithDiagnosticsAsync(member, patches);
    Assert(rejected.Explanation is null, "ungrounded response was accepted");
}

string verboseText = JsonSerializer.Serialize(new
{
    why = new
    {
        text = "DefaultFreeShippingThreshold changes the value consumed by IsFreeShipping. The causal chain is direct. This third sentence exceeds the limit.",
        citations = whyCitations,
    },
    test = new
    {
        text = "Add a focused IsFreeShipping regression test for the default threshold.",
        citations = new[] { observationCitation },
    },
});
var verboseHandler = new FakeHandler(Response(verboseText));
using (var explainer = new AnthropicExplainer(
    "proof-key",
    verboseHandler,
    "https://example.invalid/v1/messages",
    "claude-sonnet-5"))
{
    ExplanationAttempt rejected = await explainer.ExplainWithDiagnosticsAsync(member, patches);
    Assert(rejected.Explanation is null, "three-sentence causal explanation was accepted");
}

string restatedOutcomeText = JsonSerializer.Serialize(new
{
    why = new
    {
        text = "DefaultFreeShippingThreshold changes the value consumed by IsFreeShipping, so the observed result returns true instead of false.",
        citations = whyCitations,
    },
    test = new
    {
        text = "Add a focused IsFreeShipping regression test for the default threshold.",
        citations = new[] { observationCitation },
    },
});
var restatedOutcomeHandler = new FakeHandler(Response(restatedOutcomeText));
using (var explainer = new AnthropicExplainer(
    "proof-key",
    restatedOutcomeHandler,
    "https://example.invalid/v1/messages",
    "claude-sonnet-5"))
{
    ExplanationAttempt rejected = await explainer.ExplainWithDiagnosticsAsync(member, patches);
    Assert(rejected.Explanation is null, "causal explanation that restated deterministic outcomes was accepted");
}

string fabricatedCitationText = JsonSerializer.Serialize(new
{
    why = new
    {
        text = "DefaultFreeShippingThreshold makes IsFreeShipping change from false to true for 40.",
        citations = new[]
        {
            "OBSERVATION: fabricated",
            "DIFF: fabricated",
        },
    },
    test = new
    {
        text = "Test IsFreeShipping with 40 and expect false rather than true after DefaultFreeShippingThreshold.",
        citations = new[] { "OBSERVATION: fabricated" },
    },
});
var fabricatedHandler = new FakeHandler(Response(fabricatedCitationText));
using (var explainer = new AnthropicExplainer(
    "proof-key",
    fabricatedHandler,
    "https://example.invalid/v1/messages",
    "claude-sonnet-5"))
{
    ExplanationAttempt rejected = await explainer.ExplainWithDiagnosticsAsync(member, patches);
    Assert(rejected.Explanation is null, "fabricated evidence citations were accepted");
}

string wrongCaseText = acceptedText.Replace("IsFreeShipping", "isFreeShipping", StringComparison.Ordinal);
var wrongCaseHandler = new FakeHandler(Response(wrongCaseText));
using (var explainer = new AnthropicExplainer(
    "proof-key",
    wrongCaseHandler,
    "https://example.invalid/v1/messages",
    "claude-sonnet-5"))
{
    ExplanationAttempt rejected = await explainer.ExplainWithDiagnosticsAsync(member, patches);
    Assert(rejected.Explanation is null, "wrong-case grounding literal was accepted");
}

const string errorBody = "{\"type\":\"error\",\"error\":{\"message\":\"proof failure\"}}";
var errorHandler = new FakeHandler(errorBody, statusCode: HttpStatusCode.BadRequest);
using (var explainer = new AnthropicExplainer(
    "proof-key",
    errorHandler,
    "https://example.invalid/v1/messages",
    "claude-sonnet-5"))
{
    ExplanationAttempt attempt = await explainer.ExplainWithDiagnosticsAsync(member, patches);
    Assert(attempt.RawResponse == errorBody, "non-success raw response was lost");
    Assert(attempt.Explanation is null && attempt.Validation.Contains("400", StringComparison.Ordinal),
        "non-success response was not reported as rejected");
}

const string malformedBody = "not-json";
var malformedHandler = new FakeHandler(malformedBody);
using (var explainer = new AnthropicExplainer(
    "proof-key",
    malformedHandler,
    "https://example.invalid/v1/messages",
    "claude-sonnet-5"))
{
    ExplanationAttempt attempt = await explainer.ExplainWithDiagnosticsAsync(member, patches);
    Assert(attempt.RawResponse == malformedBody, "malformed raw response was lost");
    Assert(attempt.Explanation is null && attempt.Validation.Contains("malformed", StringComparison.Ordinal),
        "malformed response was not reported as rejected");
}

string marker = "<!-- proof -->";
string withoutKey = GitHubPoster.RenderSummary(
    findings.RootElement,
    marker,
    new[] { "samples/SampleApp/ShippingCalculator.cs:10" });
var explanations = new Dictionary<string, ModelExplanation>(StringComparer.Ordinal)
{
    [member.GetProperty("memberName").GetString() ?? string.Empty] = accepted!,
};
string withKey = GitHubPoster.RenderSummary(
    findings.RootElement,
    marker,
    new[] { "samples/SampleApp/ShippingCalculator.cs:10" },
    explanations);
Assert(!withoutKey.Contains("Optional model explanation", StringComparison.Ordinal), "no-key comment contains model output");
Assert(withKey.Contains("Optional model explanation", StringComparison.Ordinal), "key-enabled comment omitted accepted model output");
Assert(withKey.Contains("grounded", StringComparison.Ordinal), "model output is not labeled as grounded");
Assert(!withKey.Contains("422", StringComparison.Ordinal), "comment leaked raw GitHub 422 details");
Assert(withoutKey.StartsWith("## RealDiff: 1 behavior gap outside this diff", StringComparison.Ordinal),
    "comment does not lead with the outside-diff behavior gap count");
Assert(withoutKey.Contains("**`ShippingCalculator.IsFreeShipping` changed, but this PR didn't edit it.**", StringComparison.Ordinal),
    "comment does not lead with the unedited member");
Assert(withoutKey.Contains("<details><summary>Why, and the evidence</summary>", StringComparison.Ordinal),
    "evidence is not collapsed under details");
Assert(withoutKey.Contains("**Distinct call paths**", StringComparison.Ordinal), "deduplicated call paths section is missing");
Assert(!withoutKey.Contains("Unexpected means", StringComparison.Ordinal), "obsolete unexpected explainer remains");
Assert(!withoutKey.Contains("k__BackingField", StringComparison.Ordinal), "comment leaked compiler backing-field syntax");
Assert(withoutKey.Contains("_1 of 2 edited files exercised.", StringComparison.Ordinal), "coverage/source footer is missing");

JsonObject fullyAsserted = JsonNode.Parse(File.ReadAllText(args[0]))?.AsObject()
    ?? throw new InvalidOperationException("could not create fully asserted proof");
JsonObject fullyAssertedMember = fullyAsserted["members"]?[0]?.AsObject()
    ?? throw new InvalidOperationException("fully asserted proof has no member");
fullyAssertedMember["untestedCallSiteCount"] = 0;
fullyAssertedMember["testsWithAssertionReaction"] = fullyAssertedMember["distinctTestCount"]?.GetValue<int>() ?? 0;
fullyAssertedMember["assertionReactionSummary"] = "5 tests executed this; 5 tests had an assertion react.";
using JsonDocument fullyAssertedDocument = JsonDocument.Parse(fullyAsserted.ToJsonString());
string fullyAssertedComment = GitHubPoster.RenderSummary(
    fullyAssertedDocument.RootElement,
    marker,
    Array.Empty<string>());
Assert(fullyAssertedComment.StartsWith(
    "## RealDiff: 1 test-covered behavior change outside this diff",
    StringComparison.Ordinal),
    "fully asserted change was not labeled test-covered");
Assert(fullyAssertedComment.Contains(
    "this is a test-covered change, not an unasserted behavior gap",
    StringComparison.Ordinal),
    "fully asserted change was framed as an unasserted gap");
Assert(!fullyAssertedComment.Contains(
    "An order totaling 40 now qualifies for free shipping",
    StringComparison.Ordinal),
    "fully asserted change retained the breaking-impact lead");

JsonObject oversized = JsonNode.Parse(File.ReadAllText(args[0]))?.AsObject()
    ?? throw new InvalidOperationException("could not create oversized findings proof");
JsonArray oversizedEvidence = oversized["members"]?[0]?["evidence"]?.AsArray()
    ?? throw new InvalidOperationException("oversized proof has no evidence");
foreach (JsonNode? observation in oversizedEvidence.Take(3))
{
    observation!["baseArgs"] = new string('x', 30000);
    observation["prArgs"] = new string('y', 30000);
}
using JsonDocument oversizedDocument = JsonDocument.Parse(oversized.ToJsonString());
string budgeted = GitHubPoster.RenderSummary(
    oversizedDocument.RootElement,
    marker,
    new[] { "samples/SampleApp/ShippingCalculator.cs:10" });
Assert(budgeted.Length <= 60000, "deterministic comment exceeded GitHub body budget");
Assert(budgeted.Contains("workflow artifact", StringComparison.Ordinal), "oversized comment omitted artifact fallback");

JsonObject cleanOversized = JsonNode.Parse(oversized.ToJsonString())?.AsObject()
    ?? throw new InvalidOperationException("could not create clean budget proof");
cleanOversized["verdict"] = "clean";
cleanOversized["summary"]!["unexpectedMembers"] = 0;
cleanOversized["summary"]!["unexpectedCallSites"] = 0;
cleanOversized["members"] = new JsonArray();
using JsonDocument cleanOversizedDocument = JsonDocument.Parse(cleanOversized.ToJsonString());
string cleanBudgeted = GitHubPoster.RenderSummary(
    cleanOversizedDocument.RootElement,
    marker,
    Array.Empty<string>());
Assert(cleanBudgeted.Contains("No unexpected behavior changes", StringComparison.Ordinal), "oversized clean result lost its clean verdict");
Assert(!cleanBudgeted.Contains("not a clean result", StringComparison.OrdinalIgnoreCase), "oversized clean result was mislabeled non-clean");

using JsonDocument refusedDocument = JsonDocument.Parse(JsonSerializer.Serialize(new
{
    schema = "realdiff.findings/1",
    status = "refused",
    verdict = "could_not_analyze",
    refusal = new { reason = new string('r', 70000) },
}));
string refusedBudgeted = GitHubPoster.RenderSummary(
    refusedDocument.RootElement,
    marker,
    Array.Empty<string>());
Assert(refusedBudgeted.Length <= 60000, "refused comment exceeded GitHub body budget");
Assert(refusedBudgeted.Contains("No safety verdict was produced", StringComparison.Ordinal), "refused fallback lost non-verdict warning");

JsonObject unalignable = JsonNode.Parse(File.ReadAllText(args[0]))?.AsObject()
    ?? throw new InvalidOperationException("could not create negative ordinal proof");
JsonObject unalignableObservation = unalignable["members"]?[0]?["evidence"]?[0]?.AsObject()
    ?? throw new InvalidOperationException("negative ordinal proof has no evidence");
unalignableObservation["ordinal"] = -1;
unalignableObservation["kind"] = "CallCountChange";
unalignableObservation["detail"] = "called 2 time(s) in base, 3 in PR";
using JsonDocument unalignableDocument = JsonDocument.Parse(unalignable.ToJsonString());
string unalignableComment = GitHubPoster.RenderSummary(
    unalignableDocument.RootElement,
    marker,
    Array.Empty<string>());
Assert(unalignableComment.Contains("CallCountChange", StringComparison.Ordinal), "unalignable divergence kind was omitted");
Assert(unalignableComment.Contains("concrete values and paths are unavailable", StringComparison.Ordinal), "unalignable divergence did not explain evidence limits");
Assert(!unalignableComment.Contains("IsFreeShipping(orderTotal=Primitive:40)", StringComparison.Ordinal), "negative ordinal rendered arbitrary first-call values");

JsonObject missingOrdinal = JsonNode.Parse(unalignable.ToJsonString())?.AsObject()
    ?? throw new InvalidOperationException("could not create missing ordinal proof");
missingOrdinal["members"]?[0]?["evidence"]?[0]?.AsObject().Remove("ordinal");
using JsonDocument missingOrdinalDocument = JsonDocument.Parse(missingOrdinal.ToJsonString());
string missingOrdinalComment = GitHubPoster.RenderSummary(
    missingOrdinalDocument.RootElement,
    marker,
    Array.Empty<string>());
Assert(missingOrdinalComment.Contains("concrete values and paths are unavailable", StringComparison.Ordinal), "missing ordinal did not use unalignable rendering");
Assert(!missingOrdinalComment.Contains("IsFreeShipping(orderTotal=Primitive:40)", StringComparison.Ordinal), "missing ordinal rendered arbitrary first-call values");

var requestOrder = new List<string>();
var gitHubHandler = new GitHubHandler(requestOrder, patches[0]);
var postingModelHandler = new FakeHandler(Response(acceptedText), () => requestOrder.Add("anthropic:post"));
string eventPath = Path.GetTempFileName();
File.WriteAllText(eventPath, JsonSerializer.Serialize(new
{
    number = 1,
    pull_request = new
    {
        head = new { sha = "proof-head", repo = new { full_name = "example/repo", fork = false } },
        @base = new { repo = new { full_name = "example/repo", fork = false } },
    },
}));
string? originalEvent = Environment.GetEnvironmentVariable("GITHUB_EVENT_PATH");
string? originalRepository = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY");
string? originalToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
string? originalApi = Environment.GetEnvironmentVariable("GITHUB_API_URL");
string? originalAnthropic = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
try
{
    Environment.SetEnvironmentVariable("GITHUB_EVENT_PATH", eventPath);
    Environment.SetEnvironmentVariable("GITHUB_REPOSITORY", "example/repo");
    Environment.SetEnvironmentVariable("GITHUB_TOKEN", "proof-github-token");
    Environment.SetEnvironmentVariable("GITHUB_API_URL", "https://github.example/api/v3");
    Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "proof-anthropic-key");
    var poster = new GitHubPoster(
        gitHubHandler,
        apiKey => new AnthropicExplainer(
            apiKey,
            postingModelHandler,
            "https://anthropic.example/v1/messages",
            "claude-sonnet-5"));
    await poster.PostAsync(findings.RootElement);

    int diffFetch = requestOrder.IndexOf("github:get-files");
    int deterministicPost = requestOrder.IndexOf("github:post-summary");
    int causeComment = requestOrder.IndexOf("github:post-cause-review");
    int modelPost = requestOrder.IndexOf("anthropic:post");
    int enrichedPatch = requestOrder.IndexOf("github:patch-enriched");
    Assert(diffFetch >= 0 && diffFetch < deterministicPost && deterministicPost < causeComment
        && causeComment < modelPost && modelPost < enrichedPatch,
        "optional enrichment ran before deterministic summary posting");
    Assert(gitHubHandler.SummaryPostCount == 1, "enrichment created a duplicate summary comment");
    Assert(gitHubHandler.SummaryPatchCount == 1, "model enrichment did not update the deterministic summary comment once");
    Assert(gitHubHandler.ReviewPath == patches[0].FilePath && gitHubHandler.ReviewLine == 10,
        "cause comment did not anchor on the changed hunk's added line");
    Assert(gitHubHandler.ReviewBody.Contains("This added line is the likely cause", StringComparison.Ordinal),
        "cause comment does not explain its anchor");
    Assert(gitHubHandler.SummaryBody.Contains(
        "[Line 10 of `samples/SampleApp/SettingsParser.cs`](https://github.com/example/repo/blob/proof-head/samples/SampleApp/SettingsParser.cs#L10) changed the behavior of [line "
            + effectLine + " of `" + effectPath + "`](https://github.com/example/repo/blob/proof-head/"
            + effectPath + "#L" + effectLine + ")",
        StringComparison.Ordinal),
        "summary did not connect linked cause and effect lines");
    Assert(gitHubHandler.SummaryBody.Contains(
        "https://github.com/example/repo/blob/proof-head/" + effectPath + "#L" + effectLine,
        StringComparison.Ordinal),
        "summary did not deep-link the unedited source at the PR head");

    Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
    var multiLinePatch = new ChangedFilePatch(
        patches[0].FilePath,
        "@@ -10,1 +10,2 @@ namespace SampleApp\n+import java.util.ArrayList;\n+ordered.sort(comparator);");
    var noKeyHandler = new GitHubHandler(new List<string>(), multiLinePatch);
    bool noKeyModelCalled = false;
    var noKeyPoster = new GitHubPoster(
        noKeyHandler,
        apiKey =>
        {
            noKeyModelCalled = true;
            return new AnthropicExplainer(apiKey);
        });
    await noKeyPoster.PostAsync(findings.RootElement);
    Assert(!noKeyModelCalled, "no-key posting invoked the model client");
    Assert(noKeyHandler.ReviewLine == 11
        && noKeyHandler.ReviewBody.Contains("several added lines participate", StringComparison.Ordinal),
        "multi-line cause did not anchor on the behavioral addition and identify the hunk-level cause");
    Assert(noKeyHandler.SummaryBody.Contains(
        "https://github.com/example/repo/blob/proof-head/samples/SampleApp/SettingsParser.cs#L10-L11",
        StringComparison.Ordinal),
        "multi-line cause did not link the changed hunk range");

    using JsonDocument postedReview = JsonDocument.Parse(noKeyHandler.ReviewBody);
    string existingReview = postedReview.RootElement.GetProperty("body").GetString()
        ?? throw new InvalidOperationException("posted review body was empty");
    var movedAnchorHandler = new GitHubHandler(
        new List<string>(),
        multiLinePatch,
        existingReview,
        multiLinePatch.FilePath,
        existingReviewLine: 10);
    await new GitHubPoster(movedAnchorHandler).PostAsync(findings.RootElement);
    Assert(movedAnchorHandler.ReviewDeleteCount == 1 && movedAnchorHandler.ReviewLine == 11,
        "stale review comment was not deleted and recreated at the behavioral line");
}
finally
{
    Environment.SetEnvironmentVariable("GITHUB_EVENT_PATH", originalEvent);
    Environment.SetEnvironmentVariable("GITHUB_REPOSITORY", originalRepository);
    Environment.SetEnvironmentVariable("GITHUB_TOKEN", originalToken);
    Environment.SetEnvironmentVariable("GITHUB_API_URL", originalApi);
    Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", originalAnthropic);
    File.Delete(eventPath);
}

Console.WriteLine("=== WITHOUT ANTHROPIC_API_KEY ===");
Console.WriteLine(withoutKey);
Console.WriteLine();
Console.WriteLine("=== WITH ANTHROPIC_API_KEY (FAKE GROUNDED RESPONSE) ===");
Console.WriteLine(withKey);
Console.WriteLine();
Console.WriteLine("PASS: one request per unexpected member");
Console.WriteLine("PASS: prompt contains observations, paths, assertion reaction, and diff hunk");
Console.WriteLine("PASS: related frontier evidence requires an exact citation");
Console.WriteLine("PASS: missing/wrong-case literals and fabricated citations are rejected");
Console.WriteLine("PASS: raw non-success and malformed responses survive diagnostic validation");
Console.WriteLine("PASS: deterministic post precedes enrichment and the same comment is patched");
Console.WriteLine("PASS: cause comment anchors on the changed hunk and summary links cause/effect lines");
Console.WriteLine("PASS: stale review anchors are recreated on the behavioral line");
Console.WriteLine("PASS: no-key posting never invokes Anthropic");
Console.WriteLine("PASS: oversized evidence falls back below GitHub's body limit");
Console.WriteLine("PASS: refused and clean budget fallbacks preserve their verdicts");
Console.WriteLine("PASS: negative ordinals render kind/detail without arbitrary values");
Console.WriteLine("PASS: missing ordinals are treated as unalignable");
Console.WriteLine("verify-anthropic: PASS");
return 0;

static string Response(string text) => JsonSerializer.Serialize(new
{
    content = new[] { new { type = "text", text } },
});

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed class FakeHandler(
    string responseBody,
    Action? onRequest = null,
    HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
{
    internal int RequestCount { get; private set; }

    internal string LastRequestBody { get; private set; } = string.Empty;

    internal bool SawApiKey { get; private set; }

    internal bool SawVersion { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        onRequest?.Invoke();
        RequestCount++;
        LastRequestBody = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);
        SawApiKey = request.Headers.Contains("X-Api-Key");
        SawVersion = request.Headers.Contains("anthropic-version");
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
        };
    }
}

sealed class GitHubHandler(
    List<string> order,
    ChangedFilePatch patch,
    string? existingReviewBody = null,
    string? existingReviewPath = null,
    int? existingReviewLine = null) : HttpMessageHandler
{
    internal int SummaryPostCount { get; private set; }

    internal int SummaryPatchCount { get; private set; }

    internal string SummaryBody { get; private set; } = string.Empty;

    internal string ReviewBody { get; private set; } = string.Empty;

    internal string? ReviewPath { get; private set; }

    internal int? ReviewLine { get; private set; }

    internal int ReviewDeleteCount { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        string path = request.RequestUri?.AbsolutePath ?? string.Empty;
        string body;
        HttpStatusCode status = HttpStatusCode.OK;
        if (request.Method == HttpMethod.Get && path.EndsWith("/issues/1/comments", StringComparison.Ordinal))
        {
            body = "[]";
        }
        else if (request.Method == HttpMethod.Get && path.EndsWith("/pulls/1/comments", StringComparison.Ordinal))
        {
            body = existingReviewBody is null
                ? "[]"
                : JsonSerializer.Serialize(new[]
                {
                    new { id = 456, body = existingReviewBody, path = existingReviewPath, line = existingReviewLine },
                });
        }
        else if (request.Method == HttpMethod.Get && path.EndsWith("/pulls/1/files", StringComparison.Ordinal))
        {
            order.Add("github:get-files");
            body = JsonSerializer.Serialize(new[] { new { filename = patch.FilePath, patch = patch.Patch } });
        }
        else if (request.Method == HttpMethod.Post && path.EndsWith("/pulls/1/comments", StringComparison.Ordinal))
        {
            order.Add("github:post-cause-review");
            ReviewBody = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult() ?? string.Empty;
            using JsonDocument payload = JsonDocument.Parse(ReviewBody);
            ReviewPath = payload.RootElement.GetProperty("path").GetString();
            ReviewLine = payload.RootElement.GetProperty("line").GetInt32();
            body = "{\"id\":456}";
        }
        else if (request.Method == HttpMethod.Delete && path.EndsWith("/pulls/comments/456", StringComparison.Ordinal))
        {
            ReviewDeleteCount++;
            body = "{}";
        }
        else if (request.Method == HttpMethod.Post && path.EndsWith("/issues/1/comments", StringComparison.Ordinal))
        {
            order.Add("github:post-summary");
            SummaryPostCount++;
            using JsonDocument payload = JsonDocument.Parse(
                request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult() ?? "{}");
            SummaryBody = payload.RootElement.GetProperty("body").GetString() ?? string.Empty;
            body = "{\"id\":123}";
        }
        else if (request.Method == HttpMethod.Patch && path.EndsWith("/issues/comments/123", StringComparison.Ordinal))
        {
            SummaryPatchCount++;
            order.Add("github:patch-enriched");
            body = "{\"id\":123}";
        }
        else
        {
            throw new InvalidOperationException("Unexpected fake GitHub request: " + request.Method + " " + request.RequestUri);
        }

        return Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
    }
}
