using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RealDiff.Tracer
{
    internal sealed class RedactionPolicy
    {
        private static readonly string[] DefaultNames =
        {
            "password", "token", "secret", "key", "ssn", "email", "auth", "credential",
        };

        private static readonly Regex Jwt = new(
            @"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex AwsKey = new(
            @"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex Pem = new(
            @"-----BEGIN [A-Z0-9 ]*(?:PRIVATE KEY|CERTIFICATE)-----",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex LongBase64 = new(
            @"(?:^|[^A-Za-z0-9+/])(?:[A-Za-z0-9+/]{40,}={0,2})(?:$|[^A-Za-z0-9+/=])",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        internal RedactionPolicy(
            IReadOnlyList<string> sensitiveNames,
            IReadOnlyList<string> digestOnlyTypes,
            IReadOnlyList<string> digestOnlyPaths)
        {
            SensitiveNames = sensitiveNames;
            DigestOnlyTypes = digestOnlyTypes;
            DigestOnlyPaths = digestOnlyPaths;
        }

        internal IReadOnlyList<string> SensitiveNames { get; }

        internal IReadOnlyList<string> DigestOnlyTypes { get; }

        internal IReadOnlyList<string> DigestOnlyPaths { get; }

        internal static RedactionPolicy FromEnvironment() => new(
            MergeDefaults(Environment.GetEnvironmentVariable(TracerOptions.RedactNamesVariable)),
            ReadList(Environment.GetEnvironmentVariable(TracerOptions.RedactTypesVariable)),
            ReadList(Environment.GetEnvironmentVariable(TracerOptions.RedactPathsVariable))
                .Select(NormalizePath).ToArray());

        internal bool IsSensitiveName(string? name) => name != null
            && SensitiveNames.Any(pattern => name.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0);

        internal bool IsDigestOnlyType(Type type)
        {
            string name = type.FullName ?? type.Name;
            return DigestOnlyTypes.Any(pattern => PrefixMatch(name, pattern));
        }

        internal bool IsDigestOnlyPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            string normalized = NormalizePath(path!);
            return DigestOnlyPaths.Any(pattern =>
                normalized == pattern
                || normalized.StartsWith(pattern + "/", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/" + pattern, StringComparison.OrdinalIgnoreCase)
                || normalized.IndexOf("/" + pattern + "/", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        internal static bool ContainsCredential(string value) => Jwt.IsMatch(value)
            || AwsKey.IsMatch(value)
            || Pem.IsMatch(value)
            || LongBase64.IsMatch(value);

        private static IReadOnlyList<string> MergeDefaults(string? configured) => DefaultNames
            .Concat(ReadList(configured))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        private static IReadOnlyList<string> ReadList(string? value) => string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value!.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Trim())
                .Where(item => item.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

        private static string NormalizePath(string path) => path.Replace('\\', '/').Trim().Trim('/');

        private static bool PrefixMatch(string value, string prefix) =>
            value.Equals(prefix, StringComparison.OrdinalIgnoreCase)
            || value.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith(prefix + "`", StringComparison.OrdinalIgnoreCase);
    }
}