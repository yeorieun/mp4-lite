using System;
using System.IO;
using System.Threading;
using MP4Lite;
class ExtraTests
{
    static int Main(string[] args)
    {
        try
        {
            var engine = new Engine(Path.GetFullPath(args[0]));
            string root = Path.GetFullPath(args[1]);
            var fixture = Engine.Run(engine.Ffmpeg, new[] { "-hide_banner", "-nostdin", "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=24:duration=1", "-vf", "format=yuv420p10le", "-c:v", "libx265", "-preset", "ultrafast", "-x265-params", "log-level=error:pools=2:colorprim=9:transfer=16:colormatrix=9", Path.Combine(root, "hdr.mp4") }, CancellationToken.None, null);
            if (fixture.ExitCode != 0) throw new Exception(fixture.Error);
            var hdr = engine.ConvertFile(Path.Combine(root, "hdr.mp4"), root, Quality.Presets[1], CancellationToken.None, null);
            if (hdr.Media.Hdr || hdr.Media.Transfer != "bt709") throw new Exception("HDR tone mapping output metadata invalid");
            Console.WriteLine("PASS HDR PQ to SDR with BT.709 output metadata");
            var sar = Engine.Run(engine.Ffmpeg, new[] {"-hide_banner", "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=720x576:rate=25:duration=1", "-vf", "setsar=16/15", "-c:v", "libx264", Path.Combine(root, "anamorphic.mp4")}, CancellationToken.None, null);
            if (sar.ExitCode != 0) throw new Exception(sar.Error);
            var r = engine.ConvertFile(Path.Combine(root, "anamorphic.mp4"), root, Quality.Presets[1], CancellationToken.None, null);
            if (r.Media.Width != 640 || r.Media.Height != 480) throw new Exception("Anamorphic ratio incorrect");
            Console.WriteLine("PASS non-square pixel source aspect ratio");
            return 0;
        }
        catch(Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
