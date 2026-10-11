using AegiNext.Media.Probing;
using System.Text.Json;

namespace AegiNext.Desktop.Ui.Tests;

internal sealed record NativeScrubFixture(string Name, string Path)
{
    private static readonly string[] fixtureNames = ["1080p-h264", "4k-h264", "4k-hevc"];
    private static readonly JsonSerializerOptions jsonOptions = new() { WriteIndented = true };

    internal static async Task<IReadOnlyList<NativeScrubFixture>> CreateAsync(string artifactDirectory)
    {
        var supplied = Environment.GetEnvironmentVariable("AEGINEXT_SCRUB_MEDIA_PATH");
        if (!string.IsNullOrWhiteSpace(supplied))
        {
            Assert.True(System.IO.Path.IsPathFullyQualified(supplied) && File.Exists(supplied));
            return [new("supplied", supplied)];
        }

        var executable = MediaToolchain.ResolveFfmpeg();
        var configured = Environment.GetEnvironmentVariable("AEGINEXT_SCRUB_FIXTURES") ?? "1080p-h264,4k-h264,4k-hevc";
        var directory = System.IO.Path.Combine(artifactDirectory, "fixtures");
        Directory.CreateDirectory(directory);
        var fixtures = new List<NativeScrubFixture>();
        foreach (var name in configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            Assert.Contains(name, fixtureNames);
            var fullHd = name == "1080p-h264";
            var path = System.IO.Path.Combine(directory, name + (fullHd ? "-six-seconds-gop120.mp4" : "-ten-seconds-gop240.mp4"));
            if (!File.Exists(path))
            {
                var hevc = name == "4k-hevc";
                var arguments = new List<string>
                {
                    "-v", "error", "-nostdin", "-f", "lavfi", "-i",
                    fullHd ? "testsrc2=size=1920x1080:rate=60" : "testsrc2=size=3840x2160:rate=30",
                    "-t", fullHd ? "6" : "10", "-an", "-c:v", hevc ? "libx265" : "libx264", "-preset", "ultrafast",
                    "-crf", "22", "-threads", "2", "-g", fullHd ? "120" : "240", "-bf", "2", "-pix_fmt", "yuv420p",
                    "-vf", "setsar=1/1,setparams=range=limited:color_primaries=bt709:color_trc=bt709:colorspace=bt709",
                    "-color_range", "tv", "-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709"
                };
                if (hevc)
                {
                    arguments.AddRange(["-x265-params", "pools=2:frame-threads=2:log-level=error", "-tag:v", "hvc1"]);
                }
                arguments.AddRange(["-y", path]);
                try
                {
                    var result = await ProbeProcessRunner.RunAsync(executable, arguments, TimeSpan.FromMinutes(3),
                        65536, 65536, TestContext.Current.CancellationToken);
                    Assert.True(result.ExitCode == 0, result.StandardError);
                    var probe = await ProbeProcessRunner.RunAsync(MediaToolchain.ResolveFfprobe(),
                        ["-v", "error", "-select_streams", "v:0", "-show_entries",
                            "stream=codec_name,width,height,r_frame_rate,avg_frame_rate:format=duration:packet=pts_time,flags",
                            "-of", "json", path], TimeSpan.FromSeconds(30), 1024 * 1024, 65536, TestContext.Current.CancellationToken);
                    Assert.True(probe.ExitCode == 0, probe.StandardError);
                    using var metadata = JsonDocument.Parse(probe.StandardOutput);
                    await File.WriteAllTextAsync(path + ".metadata.json", JsonSerializer.Serialize(new
                    {
                        Encoder = executable, Arguments = arguments, Probe = metadata.RootElement,
                        ExpectedGopSeconds = fullHd ? 2 : 8
                    }, jsonOptions), TestContext.Current.CancellationToken);
                }
                catch
                {
                    File.Delete(path);
                    throw;
                }
            }
            fixtures.Add(new(name, path));
        }
        Assert.NotEmpty(fixtures);
        return fixtures;
    }
}
