using System.Net;
using System.Text;
using System.Text.Json;
using AegiNext.Desktop.Updates;

namespace AegiNext.Desktop.Tests.Updates;

internal static class UpdateTestData
{
    internal static UpdateRelease Release(string version = "0.8.0.0")
    {
        return new(new(version), "rel/beta/" + version, version + " Beta", "## Changes\n- Fixed playback",
            new("https://github.com/Yohuke-no-Symphony/Aegisub-NEXT/releases/tag/rel/beta/" + version));
    }

    internal static string Json(string tag = "rel/beta/0.8.0.0", bool prerelease = true, bool draft = false,
        string? name = "0.8.0 Beta", string? body = "## Changes\n- Fixed playback", string? url = null)
    {
        return JsonSerializer.Serialize(new
        {
            tag_name = tag, prerelease, draft, name, body,
            html_url = url ?? "https://github.com/Yohuke-no-Symphony/Aegisub-NEXT/releases/tag/" + tag
        });
    }

    internal static HttpResponseMessage Response(string json, string? next = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (next is not null)
        {
            response.Headers.Add("Link", "<" + next + ">; rel=\"next\"");
        }
        return response;
    }
}
