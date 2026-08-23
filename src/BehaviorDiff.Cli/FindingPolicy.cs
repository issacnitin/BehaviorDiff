using System;
using System.Linq;
using System.Text.Json;

namespace BehaviorDiff.Cli
{
    internal static class FindingPolicy
    {
        internal static bool IsStrict(JsonElement findings) =>
            findings.TryGetProperty("commentPolicy", out JsonElement policy)
            && string.Equals(String(policy, "mode"), "strict", StringComparison.Ordinal);

        internal static bool IsCommentEligible(JsonElement findings, JsonElement member)
        {
            if (BaselinePolicy.IsSuppressed(member))
            {
                return false;
            }

            if (IsStrict(findings))
            {
                return true;
            }

            return !member.TryGetProperty("defaultCommentEligible", out JsonElement eligible)
                || eligible.ValueKind != JsonValueKind.False;
        }

        internal static JsonElement[] EligibleUnexpected(JsonElement findings) =>
            findings.TryGetProperty("members", out JsonElement members)
                ? members.EnumerateArray().Where(member =>
                    String(member, "attribution") == "unexpected" && IsCommentEligible(findings, member)).ToArray()
                : Array.Empty<JsonElement>();

        private static string String(JsonElement element, string property) =>
            element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
    }
}