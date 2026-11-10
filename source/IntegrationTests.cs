using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Web.Script.Serialization;
using MP4Lite;

class IntegrationTests
{
    static Engine engine;
    static string root;
    static List<string> passed = new List<string>();
    static void Assert(bool ok, string message) { if (!ok) throw new Exception(message); }
    static void Pass(string name) { passed.Add(name); Console.WriteLine("PASS " + name); }
    static string Hash(string path) { using (var h = SHA256.Create()) using (var s = File.OpenRead(path)) return BitConverter.ToString(h.ComputeHash(s)); }
    static string Run(params string[] args)
    {
        var r = Engine.Run(engine.Ffmpeg, new[] { "-hide_banner", "-nostdin", "-y", "-loglevel", "error" }.Concat(args), CancellationToken.None, null);
        Assert(r.ExitCode == 0, "Fixture generation failed: " + r.Error); return r.Output;
    }
    static string AudioHash(string path)
    {
        var r = Engine.Run(engine.Ffprobe, new[] { "-v", "error", "-select_streams", "a", "-show_packets", "-show_data_hash", "sha256", "-show_entries", "packet=stream_index,data_hash", "-of", "compact=p=0:nk=0", path }, CancellationToken.None, null);
        Assert(r.ExitCode == 0, r.Error); return r.Output;
    }
    static string VideoTiming(string path)
    {
        var r = Engine.Run(engine.Ffprobe, new[] { "-v", "error", "-select_streams", "v:0", "-show_frames", "-show_entries", "frame=best_effort_timestamp_time", "-of", "csv=p=0", path }, CancellationToken.None, null);
        Assert(r.ExitCode == 0, r.Error);
        return String.Join("\n", r.Output.Split('\n').Select(s => s.Trim().TrimEnd(',')).Where(s => s.Length > 0));
    }
    static int Main(string[] args)
    {
        try
        {
            engine = new Engine(Path.GetFullPath(args[0])); root = Path.GetFullPath(args[1]); Directory.CreateDirectory(root);
            string input = Path.Combine(root, "한글 & sample (원본).mp4");
            Run("-f", "lavfi", "-i", "testsrc2=size=1920x1080:rate=24:duration=6", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=6", "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=44100:duration=6", "-map", "0:v", "-map", "1:a", "-map", "2:a", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "16", "-c:a", "aac", "-b:a", "160k", "-metadata:s:a:0", "language=kor", "-metadata:s:a:1", "language=eng", input);
            string beforeHash = Hash(input), audioHash = AudioHash(input), timing = VideoTiming(input);
            var results = new List<object>();
            for (int i = 0; i < Quality.Presets.Length; i++)
            {
                int maxProgress = 0;
                var result = engine.ConvertFile(input, root, Quality.Presets[i], CancellationToken.None, (p, phase) => maxProgress = Math.Max(maxProgress, p));
                var expected = Engine.Dimensions(new MediaInfo { Width = 1920, Height = 1080 }, Quality.Presets[i]);
                Assert(result.Media.Width == expected[0] && result.Media.Height == expected[1], "Wrong resolution");
                Assert(result.Media.Audio.Count == 2, "Lost audio track");
                Assert(AudioHash(result.Path) == audioHash, "Audio packet bytes changed");
                Assert(VideoTiming(result.Path) == timing, "Video frame timestamps changed");
                Assert(result.After < result.Before, "Fixture did not shrink");
                Assert(maxProgress == 100, "Progress did not complete");
                results.Add(new { preset = Quality.Presets[i].Suffix, inputBytes = result.Before, outputBytes = result.After, width = result.Media.Width, height = result.Media.Height, audioPacketHashesEqual = true, frameTimestampsEqual = true });
                Pass(Quality.Presets[i].Suffix + ": compression, TWO audio tracks bit-exact, frame timing, progress, Korean path");
            }
            Assert(Hash(input) == beforeHash, "Original modified"); Pass("Original unchanged (SHA-256)");
            string existing = Path.Combine(root, "한글 & sample (원본)_480p.mp4"); string existingHash = Hash(existing);
            var duplicate = engine.ConvertFile(input, root, Quality.Presets[1], CancellationToken.None, null);
            Assert(duplicate.Path != existing && Hash(existing) == existingHash, "Collision overwrote file"); Pass("Duplicate output names never overwrite");
            string silent = Path.Combine(root, "silent-small.mp4");
            Run("-f", "lavfi", "-i", "testsrc2=size=320x180:rate=15:duration=1", "-c:v", "libx264", "-crf", "18", silent);
            var small = engine.ConvertFile(silent, root, Quality.Presets[0], CancellationToken.None, null);
            Assert(small.Media.Width == 320 && small.Media.Height == 180 && small.Media.Audio.Count == 0, "Silent small video failed"); Pass("Silent video / no upscaling");
            string rotated = Path.Combine(root, "rotated.mp4");
            Run("-display_rotation:v:0", "90", "-i", input, "-map", "0", "-c", "copy", rotated);
            var portrait = engine.ConvertFile(rotated, root, Quality.Presets[1], CancellationToken.None, null);
            Assert(portrait.Media.Width == 480 && portrait.Media.Height == 852, "Rotation not handled: " + portrait.Media.Width + "x" + portrait.Media.Height);
            Assert(AudioHash(portrait.Path) == AudioHash(rotated), "Rotated audio changed"); Pass("Phone rotation and portrait dimensions");
            string bad = Path.Combine(root, "broken.mp4"); File.WriteAllText(bad, "invalid test input"); bool failed = false;
            try { engine.ConvertFile(bad, root, Quality.Presets[1], CancellationToken.None, null); } catch (InvalidOperationException) { failed = true; }
            Assert(failed && !File.Exists(Path.Combine(root, "broken_480p.mp4")), "Broken file accepted"); Pass("Corrupt file failure without partial output");
            string cancelDir = Path.Combine(root, "cancelled"); bool cancelled = false;
            using (var ct = new CancellationTokenSource())
            {
                try { engine.ConvertFile(input, cancelDir, Quality.Presets[0], ct.Token, (p, phase) => { if (p > 0 && p < 98) ct.Cancel(); }); }
                catch (OperationCanceledException) { cancelled = true; }
            }
            Assert(cancelled && Directory.GetFiles(cancelDir).Length == 0, "Cancellation left files"); Pass("Cancellation stops running encoder and removes temp file");
            var retry = engine.ConvertFile(silent, cancelDir, Quality.Presets[2], CancellationToken.None, null);
            Assert(File.Exists(retry.Path), "Conversion failed after cancellation"); Pass("Conversion succeeds after cancellation");
            string vfr = Path.Combine(root, "vfr.mp4");
            Run("-f", "lavfi", "-i", "testsrc2=size=640x360:rate=30:duration=3", "-vf", "select='if(lt(t,1),1,not(mod(n,3)))'", "-fps_mode", "vfr", "-c:v", "libx264", vfr);
            var vfrOut = engine.ConvertFile(vfr, root, Quality.Presets[1], CancellationToken.None, null);
            Assert(VideoTiming(vfr) == VideoTiming(vfrOut.Path), "VFR timing changed"); Pass("Variable frame timing preserved");
            Assert(Directory.GetFiles(root, ".mp4lite-*", SearchOption.AllDirectories).Length == 0, "Temp residue");
            File.WriteAllText(Path.Combine(root, "test-report.json"), new JavaScriptSerializer().Serialize(new { passed = passed, conversions = results }));
            Console.WriteLine("ALL " + passed.Count + " CHECKS PASSED"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
