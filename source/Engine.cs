using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace MP4Lite
{
    public sealed class Quality
    {
        public readonly string Name, Suffix;
        public readonly int LongEdge, ShortEdge, Crf;
        public Quality(string name, string suffix, int longEdge, int shortEdge, int crf)
        { Name = name; Suffix = suffix; LongEdge = longEdge; ShortEdge = shortEdge; Crf = crf; }
        public static readonly Quality[] Presets = {
            new Quality("가볍게 · 720p", "720p", 1280, 720, 28),
            new Quality("작게 · 480p", "480p", 854, 480, 32),
            new Quality("아주 작게 · 360p", "360p", 640, 360, 36)
        };
    }

    public sealed class AudioInfo
    {
        public string Codec, SampleRate;
        public int Channels;
    }
    public sealed class MediaInfo
    {
        public int VideoIndex = -1, Width, Height;
        public double Duration;
        public bool Hdr;
        public string Transfer;
        public readonly List<AudioInfo> Audio = new List<AudioInfo>();
    }
    public sealed class ConversionResult
    {
        public string Path;
        public long Before, After;
        public MediaInfo Media;
    }
    public sealed class ProcessResult
    {
        public int ExitCode;
        public string Output, Error;
    }

    public sealed class Engine
    {
        public readonly string Ffmpeg, Ffprobe;
        public Engine(string engineDir)
        {
            Ffmpeg = Path.Combine(engineDir, "ffmpeg.exe");
            Ffprobe = Path.Combine(engineDir, "ffprobe.exe");
        }
        public void CheckInstalled()
        {
            if (!File.Exists(Ffmpeg) || !File.Exists(Ffprobe))
                throw new InvalidOperationException("변환 엔진을 찾을 수 없습니다. ZIP 전체를 압축 해제한 뒤 실행해 주세요.\r\nMP4Lite.exe 옆의 engine 폴더가 필요합니다.");
        }

        // Windows CommandLineToArgvW-compatible escaping. No shell is used.
        public static string Quote(string value)
        {
            var sb = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') { sb.Append('\\', slashes * 2 + 1); sb.Append('"'); }
                else { sb.Append('\\', slashes); sb.Append(c); }
                slashes = 0;
            }
            sb.Append('\\', slashes * 2); sb.Append('"');
            return sb.ToString();
        }

        public static ProcessResult Run(string exe, IEnumerable<string> args, CancellationToken token, Action<string> onOutput)
        {
            token.ThrowIfCancellationRequested();
            var output = new StringBuilder();
            var error = new StringBuilder();
            var psi = new ProcessStartInfo(exe, String.Join(" ", args.Select(Quote))) {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            using (var p = new Process { StartInfo = psi })
            {
                p.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) {
                    if (e.Data == null) return;
                    lock (output) { if (output.Length < 8 * 1024 * 1024) output.AppendLine(e.Data); }
                    if (onOutput != null) onOutput(e.Data);
                };
                p.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) {
                    if (e.Data == null) return;
                    lock (error) {
                        error.AppendLine(e.Data);
                        if (error.Length > 24000) error.Remove(0, error.Length - 16000);
                    }
                };
                p.Start(); p.BeginOutputReadLine(); p.BeginErrorReadLine();
                try
                {
                    while (!p.WaitForExit(100)) token.ThrowIfCancellationRequested();
                    p.WaitForExit(); // Drain redirected event handlers before reading buffers.
                    token.ThrowIfCancellationRequested();
                    return new ProcessResult { ExitCode = p.ExitCode, Output = output.ToString(), Error = error.ToString() };
                }
                finally
                {
                    if (!p.HasExited) { try { p.Kill(); } catch (InvalidOperationException) { } p.WaitForExit(); }
                }
            }
        }

        static string Str(IDictionary<string, object> d, string key)
        { object value; return d.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : ""; }
        static double Num(IDictionary<string, object> d, string key)
        { double v; return Double.TryParse(Str(d, key), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0; }

        public MediaInfo Probe(string path, CancellationToken token)
        {
            CheckInstalled();
            var r = Run(Ffprobe, new[] { "-v", "error", "-show_streams", "-show_format", "-of", "json", path }, token, null);
            if (r.ExitCode != 0) throw new InvalidOperationException("MP4 파일을 읽을 수 없습니다. 파일이 손상되었거나 지원되지 않는 형식입니다.\r\n" + r.Error);
            var json = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 }.Deserialize<Dictionary<string, object>>(r.Output);
            var info = new MediaInfo();
            object streamValue;
            if (!json.TryGetValue("streams", out streamValue)) throw new InvalidOperationException("영상 트랙이 없는 파일입니다.");
            foreach (Dictionary<string, object> s in (IEnumerable)streamValue)
            {
                string type = Str(s, "codec_type");
                if (type == "audio") info.Audio.Add(new AudioInfo { Codec = Str(s, "codec_name"), SampleRate = Str(s, "sample_rate"), Channels = (int)Num(s, "channels") });
                if (type != "video" || info.VideoIndex >= 0) continue;
                object disp;
                if (s.TryGetValue("disposition", out disp) && Num((Dictionary<string, object>)disp, "attached_pic") == 1) continue;
                info.VideoIndex = (int)Num(s, "index");
                double width = Num(s, "width"), height = Num(s, "height");
                string[] sar = Str(s, "sample_aspect_ratio").Split(':');
                double a, b;
                if (sar.Length == 2 && Double.TryParse(sar[0], out a) && Double.TryParse(sar[1], out b) && a > 0 && b > 0) width *= a / b;
                double rotation = 0;
                object sides;
                if (s.TryGetValue("side_data_list", out sides))
                    foreach (Dictionary<string, object> side in (IEnumerable)sides)
                        if (side.ContainsKey("rotation")) rotation = Num(side, "rotation");
                if (Math.Abs(rotation % 180) > 45 && Math.Abs(rotation % 180) < 135) { double swap = width; width = height; height = swap; }
                info.Width = Math.Max(2, (int)Math.Round(width)); info.Height = Math.Max(2, (int)Math.Round(height));
                info.Duration = Num(s, "duration");
                info.Transfer = Str(s, "color_transfer");
                info.Hdr = info.Transfer == "smpte2084" || info.Transfer == "arib-std-b67";
            }
            object format;
            if (info.Duration <= 0 && json.TryGetValue("format", out format)) info.Duration = Num((Dictionary<string, object>)format, "duration");
            if (info.VideoIndex < 0) throw new InvalidOperationException("압축할 영상 트랙이 없는 파일입니다.");
            return info;
        }

        public static int[] Dimensions(MediaInfo media, Quality quality)
        {
            double maxW = media.Width >= media.Height ? quality.LongEdge : quality.ShortEdge;
            double maxH = media.Width >= media.Height ? quality.ShortEdge : quality.LongEdge;
            double scale = Math.Min(1, Math.Min(maxW / media.Width, maxH / media.Height));
            return new[] { Math.Max(2, (int)(media.Width * scale / 2) * 2), Math.Max(2, (int)(media.Height * scale / 2) * 2) };
        }

        public static List<string> Arguments(string input, string temp, MediaInfo media, Quality quality)
        {
            int[] size = Dimensions(media, quality);
            string filter = "scale=" + size[0] + ":" + size[1] + ":flags=lanczos,setsar=1";
            if (media.Hdr)
                filter = "zscale=transfer=linear:npl=100,format=gbrpf32le,tonemap=tonemap=hable:desat=0,zscale=primaries=bt709:transfer=bt709:matrix=bt709:range=limited," + filter;
            var args = new List<string> { "-hide_banner", "-nostdin", "-n", "-loglevel", "warning", "-i", input,
                "-map", "0:" + media.VideoIndex, "-map", "0:a?", "-map_metadata", "0", "-map_chapters", "0",
                "-vf", filter, "-c:v", "libx264", "-preset", "medium", "-crf", quality.Crf.ToString(CultureInfo.InvariantCulture),
                "-pix_fmt", "yuv420p", "-fps_mode", "passthrough", "-c:a", "copy", "-metadata:s:v:0", "rotate=0",
                "-movflags", "+faststart", "-max_muxing_queue_size", "4096", "-progress", "pipe:1", "-nostats" };
            if (media.Hdr) args.AddRange(new[] { "-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709" });
            args.AddRange(new[] { "-f", "mp4", temp });
            return args;
        }

        public ConversionResult ConvertFile(string input, string directory, Quality quality, CancellationToken token, Action<int, string> progress)
        {
            CheckInstalled(); token.ThrowIfCancellationRequested();
            input = Path.GetFullPath(input);
            if (!String.Equals(Path.GetExtension(input), ".mp4", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("MP4 파일만 추가할 수 있습니다.");
            if (!File.Exists(input)) throw new FileNotFoundException("원본 파일을 찾을 수 없습니다.", input);
            Directory.CreateDirectory(directory);
            long before = new FileInfo(input).Length;
            if (progress != null) progress(0, "영상 정보 확인 중");
            MediaInfo media = Probe(input, token);
            string temp = Path.Combine(directory, ".mp4lite-" + Guid.NewGuid().ToString("N") + ".tmp.mp4");
            try
            {
                var result = Run(Ffmpeg, Arguments(input, temp, media, quality), token, delegate(string line) {
                    if (!line.StartsWith("out_time_us=", StringComparison.Ordinal)) return;
                    double us;
                    if (Double.TryParse(line.Substring(12), NumberStyles.Float, CultureInfo.InvariantCulture, out us) && media.Duration > 0 && progress != null)
                        progress((int)Math.Min(98, Math.Max(0, us / 1000000 / media.Duration * 98)), "화질 압축 중 · 오디오 원본 복사");
                });
                if (result.ExitCode != 0) throw new InvalidOperationException("변환에 실패했습니다. 오디오는 다른 형식으로 바꾸지 않습니다.\r\n" + result.Error);
                token.ThrowIfCancellationRequested();
                if (progress != null) progress(99, "결과 파일 확인 중");
                MediaInfo check = Probe(temp, token);
                if (check.Audio.Count != media.Audio.Count) throw new InvalidOperationException("오디오 트랙 검증에 실패했습니다. 결과를 저장하지 않았습니다.");
                for (int i = 0; i < media.Audio.Count; i++)
                    if (check.Audio[i].Codec != media.Audio[i].Codec || check.Audio[i].Channels != media.Audio[i].Channels || check.Audio[i].SampleRate != media.Audio[i].SampleRate)
                        throw new InvalidOperationException("오디오 형식 검증에 실패했습니다. 결과를 저장하지 않았습니다.");
                long after = new FileInfo(temp).Length;
                string stem = Path.GetFileNameWithoutExtension(input);
                if (stem.Length > 120) stem = stem.Substring(0, 120);
                stem += "_" + quality.Suffix;
                string dest = null;
                for (int n = 0; ; n++)
                {
                    token.ThrowIfCancellationRequested();
                    dest = Path.Combine(directory, stem + (n == 0 ? "" : " (" + n + ")") + ".mp4");
                    if (File.Exists(dest) || Directory.Exists(dest)) continue;
                    try { File.Move(temp, dest); break; }
                    catch (IOException) { if (!File.Exists(dest) && !Directory.Exists(dest)) throw; }
                }
                if (progress != null) progress(100, "완료");
                return new ConversionResult { Path = dest, Before = before, After = after, Media = check };
            }
            finally
            {
                if (File.Exists(temp)) { try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            }
        }
    }
}
