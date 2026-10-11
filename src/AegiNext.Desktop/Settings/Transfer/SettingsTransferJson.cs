using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace AegiNext.Desktop.Settings.Transfer;

internal static class SettingsTransferJson
{
    private static readonly UTF8Encoding strictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions options = CreateOptions();

    internal static byte[] Serialize<T>(T value, int maximumBytes)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, options);
        if (bytes.Length > maximumBytes)
        {
            throw new InvalidDataException("The settings document exceeds its size limit.");
        }

        return bytes;
    }

    internal static T Deserialize<T>(ReadOnlySpan<byte> bytes, int maximumBytes)
    {
        if (bytes.Length > maximumBytes)
        {
            throw new InvalidDataException("The settings document exceeds its size limit.");
        }

        try
        {
            _ = strictUtf8.GetCharCount(bytes);
            using var document = JsonDocument.Parse(bytes.ToArray(), new() { MaxDepth = 32 });
            ValidateKeys(document.RootElement);
            return document.RootElement.Deserialize<T>(options)
                ?? throw new InvalidDataException("The settings document is empty.");
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException)
        {
            throw new InvalidDataException("The settings document is not valid UTF-8 JSON.", error);
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Kind == JsonTypeInfoKind.Object)
            {
                foreach (var property in info.Properties)
                {
                    var optionalUpdatePreference = info.Type == typeof(WorkbenchPreferences) &&
                        property.Name is nameof(WorkbenchPreferences.AutoCheckUpdates) or nameof(WorkbenchPreferences.UpdateChannel);
                    property.IsRequired = property.Get is not null && property.Set is not null && !optionalUpdatePreference;
                }
            }
        });
        return new()
        {
            WriteIndented = true,
            MaxDepth = 32,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true,
            TypeInfoResolver = resolver
        };
    }

    private static void ValidateKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException($"Duplicate settings field: {property.Name}.");
                }

                ValidateKeys(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                ValidateKeys(item);
            }
        }
    }
}
