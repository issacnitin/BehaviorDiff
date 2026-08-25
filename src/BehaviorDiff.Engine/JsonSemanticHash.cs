using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BehaviorDiff.Engine
{
    internal static class JsonSemanticHash
    {
        internal static string Compute(string path)
        {
            using FileStream stream = File.OpenRead(path);
            using JsonDocument document = JsonDocument.Parse(stream);
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Append(hash, document.RootElement);
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }

        internal static IReadOnlyList<KeyValuePair<string, string>> ComputeTopLevel(string path)
        {
            using FileStream stream = File.OpenRead(path);
            using JsonDocument document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new DiffInputException("Semantic hash input root must be an object.");
            }

            return document.RootElement.EnumerateObject()
                .Where(property => property.Name != "generatedUtc")
                .OrderBy(property => property.Name, StringComparer.Ordinal)
                .Select(property => new KeyValuePair<string, string>(property.Name, Hash(property.Value)))
                .ToArray();
        }

        internal static string ExtractTopLevel(string path, string propertyName)
        {
            using FileStream stream = File.OpenRead(path);
            using JsonDocument document = JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty(propertyName, out JsonElement property))
            {
                throw new DiffInputException("Top-level JSON property not found: " + propertyName);
            }

            return property.GetRawText();
        }

        private static string Hash(JsonElement element)
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Append(hash, element);
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }

        private static void Append(IncrementalHash hash, JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    Token(hash, "{");
                    foreach (JsonProperty property in element.EnumerateObject()
                        .Where(property => property.Name != "generatedUtc")
                        .OrderBy(property => property.Name, StringComparer.Ordinal))
                    {
                        Token(hash, "P");
                        Token(hash, property.Name);
                        Append(hash, property.Value);
                    }
                    Token(hash, "}");
                    break;
                case JsonValueKind.Array:
                    Token(hash, "[");
                    foreach (JsonElement item in element.EnumerateArray())
                    {
                        Append(hash, item);
                    }
                    Token(hash, "]");
                    break;
                case JsonValueKind.String:
                    Token(hash, "S");
                    Token(hash, element.GetString() ?? string.Empty);
                    break;
                case JsonValueKind.Number:
                    Token(hash, "N");
                    Token(hash, element.GetRawText());
                    break;
                case JsonValueKind.True:
                    Token(hash, "T");
                    break;
                case JsonValueKind.False:
                    Token(hash, "F");
                    break;
                case JsonValueKind.Null:
                    Token(hash, "0");
                    break;
                default:
                    throw new DiffInputException("Unsupported JSON value in " + element.ValueKind + ".");
            }
        }

        private static void Token(IncrementalHash hash, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            Span<byte> length = stackalloc byte[4];
            BitConverter.TryWriteBytes(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
    }
}