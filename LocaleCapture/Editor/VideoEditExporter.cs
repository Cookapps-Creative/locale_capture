using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CookApps.LocaleCapture.Editor
{
    // 편집 지시(EditList)를 한 언어 영상에 적용해 새 파일로 내보낸다. 원본 타임라인에서 삭제 구간을 빼고,
    // 배속을 걸고, 삽입 지점에 클립을 끼우고, 음소거 구간의 소리를 지운 뒤 하나로 잇는다. ffmpeg filter_complex 한 번에 처리한다.
    // BGM·SFX 스템(audio_bgm.wav·audio_sfx.wav)이 있으면 영상의 소리 대신 스템을 섞어 쓰고, 그래야 BGM/SFX 를 따로 지울 수 있다.
    internal static class VideoEditExporter
    {
        private struct Item
        {
            public bool IsClip;
            public float Start, End;   // 원본 구간
            public float Rate;         // 원본 구간 배속
            public string File;        // 클립
            public bool MuteBgm, MuteSfx;
            public List<EditList.Mute> Mutes;   // 클립 안 음소거(클립 안 시각)
            public string InsertId;    // 클립의 삽입 Id(페이드 끝점 찾기용)
            public float At;           // 클립이 실제로 놓인 원본 시각
            public float ClipStart, ClipEnd;   // 클립에서 쓰는 조각(클립 안 삭제 구간을 뺀 것)
        }

        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
        private static string F(float v) => v.ToString("0.####", Ci);

        // 비율(aw:ah)의 출력 크기. 짧은 변은 녹화 설정의 해상도 프리셋, 없으면 1080.
        public static void RatioSize(int aw, int ah, CaptureSettings settings, out int width, out int height)
        {
            int shortSide;
            switch (settings.Resolution)
            {
                case OutputResolution.P720: shortSide = 720; break;
                case OutputResolution.P1440: shortSide = 1440; break;
                case OutputResolution.P2160: shortSide = 2160; break;
                default: shortSide = 1080; break;
            }
            if (aw <= ah) { width = shortSide; height = (int) Math.Round(shortSide * (double) ah / aw); }
            else { height = shortSide; width = (int) Math.Round(shortSide * (double) aw / ah); }
            width &= ~1;
            height &= ~1;
        }

        public static string Extension(CaptureSettings settings) => settings.Format == VideoFormat.WebM ? "webm" : "mp4";

        // 녹화 설정의 포맷·품질을 ffmpeg 인코더 인자로.
        private static string EncoderArgs(CaptureSettings s)
        {
            int mbps = Math.Max(1, s.CustomBitrateMbps);
            if (s.Format == VideoFormat.WebM)
            {
                string q = s.Quality == VideoQuality.Custom ? $"-b:v {mbps}M"
                    : $"-crf {(s.Quality == VideoQuality.Low ? 40 : s.Quality == VideoQuality.Medium ? 34 : 28)} -b:v 0";
                return $"-c:v libvpx-vp9 -row-mt 1 -deadline good -cpu-used 4 {q} -pix_fmt yuv420p -c:a libopus -b:a 160k";
            }
            string h = s.Quality == VideoQuality.Custom ? $"-b:v {mbps}M -maxrate {mbps}M -bufsize {mbps * 2}M"
                : $"-crf {(s.Quality == VideoQuality.Low ? 28 : s.Quality == VideoQuality.Medium ? 23 : 18)}";
            return $"-c:v libx264 -preset veryfast {h} -pix_fmt yuv420p -c:a aac -b:a 192k -movflags +faststart";
        }

        // 성공하면 빈 문자열, 실패하면 ffmpeg 오류. bgmStem/sfxStem 은 없으면 null.
        // frameWidth/frameHeight 가 0보다 크면 그 크기로 내보낸다. 비율이 다르면 framing 대로 자르거나 블러로 채운다.
        public static string Export(string sourceFile, string locale, EditList edits, string outputFile, string bgmStem, string sfxStem,
            CaptureSettings encode, int frameWidth, int frameHeight, EditList.Framing framing, Action<float> progress)
        {
            string probeText = BundledFfmpeg.Run($"-hide_banner -i \"{sourceFile}\"") ?? "";
            BundledFfmpeg.MediaInfo src = BundledFfmpeg.Probe(sourceFile);
            if (!src.HasVideo || src.Duration <= 0) return "원본을 읽지 못했다: " + sourceFile;
            int fps = 30;
            Match fpsMatch = Regex.Match(probeText, @"(\d+(?:\.\d+)?) fps");
            if (fpsMatch.Success) fps = (int) Math.Round(float.Parse(fpsMatch.Groups[1].Value, Ci));

            List<Item> items = BuildTimeline(src.Duration, edits, locale);
            if (items.Count == 0) return "남는 구간이 없다";

            var inputs = new List<string> { $"-i \"{sourceFile}\"" };
            var f = new StringBuilder();

            // 출력 크기. 조각마다 이 크기로 맞춘 뒤 잇는다(클립도 자기 비율에서 바로 맞춰 검은 띠가 생기지 않게).
            int outW = frameWidth > 0 && frameHeight > 0 ? frameWidth : src.Width & ~1;
            int outH = frameWidth > 0 && frameHeight > 0 ? frameHeight : src.Height & ~1;

            // 클립 정보와 조각 길이
            var clipInfo = new Dictionary<string, BundledFfmpeg.MediaInfo>();
            foreach (Item it in items.Where(i => i.IsClip))
            {
                if (clipInfo.ContainsKey(it.File)) continue;
                BundledFfmpeg.MediaInfo info = BundledFfmpeg.Probe(it.File);
                if (!info.HasVideo) return "클립을 읽지 못했다: " + it.File;
                clipInfo[it.File] = info;
            }
            // 클립은 클립 안 삭제 구간을 뺀 조각들로, 다시 클립 안 배속 경계에서 나눈다(전부 지웠으면 빠진다).
            var pieces = new List<Item>();
            foreach (Item it in items)
            {
                if (!it.IsClip) { pieces.Add(it); continue; }
                float clipLength = clipInfo[it.File].Duration;
                EditList.Insert ins = edits.InsertById(it.InsertId);
                List<(float start, float end)> keep = ins != null ? ins.Keep(clipLength) : new List<(float, float)> { (0, clipLength) };
                foreach ((float ks, float ke) in keep)
                {
                    if (ke - ks < 0.001f) continue;
                    List<(float start, float end, float rate)> parts = ins != null ? ins.SpeedPieces(ks, ke) : new List<(float, float, float)> { (ks, ke, 1f) };
                    foreach ((float ps, float pe, float pr) in parts)
                    {
                        if (pe - ps < 0.001f) continue;
                        Item p = it;
                        p.ClipStart = ps;
                        p.ClipEnd = pe;
                        p.Rate = pr > 0 ? pr : 1f;
                        pieces.Add(p);
                    }
                }
            }
            items = pieces;
            if (items.Count == 0) return "남는 구간이 없다";
            float[] lengths = items.Select(it => it.IsClip ? (it.ClipEnd - it.ClipStart) / it.Rate : (it.End - it.Start) / it.Rate).ToArray();
            float total = lengths.Sum();

            // 원본 소리: 스템이 있으면 스템을 섞고(BGM 교체면 BGM 스템은 뺀다), 없으면 영상의 소리. 음소거는 여기서 먼저 적용한다.
            bool hasBgm = bgmStem != null && File.Exists(bgmStem);
            bool hasSfx = sfxStem != null && File.Exists(sfxStem);
            string sourceAudio = null;
            if (hasBgm || hasSfx)
            {
                var pads = new List<string>();
                if (hasBgm)
                {
                    inputs.Add($"-i \"{bgmStem}\"");
                    f.Append($"[{inputs.Count - 1}:a]aformat=sample_rates=48000:channel_layouts=stereo{MuteFilter(edits, MuteTarget.Bgm)}[bgm];");
                    pads.Add("[bgm]");
                }
                if (hasSfx)
                {
                    inputs.Add($"-i \"{sfxStem}\"");
                    f.Append($"[{inputs.Count - 1}:a]aformat=sample_rates=48000:channel_layouts=stereo{MuteFilter(edits, MuteTarget.Sfx)}[sfx];");
                    pads.Add("[sfx]");
                }
                if (pads.Count == 2) f.Append("[bgm][sfx]amix=inputs=2:duration=longest:normalize=0[mixed];");
                sourceAudio = pads.Count == 2 ? "[mixed]" : pads.Count == 1 ? pads[0] : null;
            }
            else if (src.HasAudio)
            {
                f.Append($"[0:a]aformat=sample_rates=48000:channel_layouts=stereo{MuteFilter(edits, MuteTarget.All, true)}[mixed];");
                sourceAudio = "[mixed]";
            }

            int sourceCount = items.Count(i => !i.IsClip);
            if (sourceCount > 0)
            {
                f.Append($"[0:v]split={sourceCount}");
                for (int i = 0; i < sourceCount; i++) f.Append($"[sv{i}]");
                f.Append(';');
                if (sourceAudio != null)
                {
                    f.Append($"{sourceAudio}asplit={sourceCount}");
                    for (int i = 0; i < sourceCount; i++) f.Append($"[sa{i}]");
                    f.Append(';');
                }
            }

            var concatPads = new StringBuilder();
            int srcIndex = 0;
            for (int k = 0; k < items.Count; k++)
            {
                Item it = items[k];
                float len = lengths[k];
                if (!it.IsClip)
                {
                    f.Append($"[sv{srcIndex}]trim=start={F(it.Start)}:end={F(it.End)},setpts=(PTS-STARTPTS)/{F(it.Rate)}[t{k}];");
                    f.Append(FrameFilter($"[t{k}]", src.Width, src.Height, outW, outH, framing.Crop, framing.Focus, $"[f{k}]", k.ToString()) + ";");
                    f.Append($"[f{k}]fps={fps},format=yuv420p[v{k}];");
                    if (sourceAudio != null)
                        f.Append($"[sa{srcIndex}]atrim=start={F(it.Start)}:end={F(it.End)},asetpts=PTS-STARTPTS{Atempo(it.Rate)}[a{k}];");
                    else
                    {
                        inputs.Add($"-f lavfi -t {F(len)} -i anullsrc=r=48000:cl=stereo");
                        f.Append($"[{inputs.Count - 1}:a]anull[a{k}];");
                    }
                    srcIndex++;
                }
                else
                {
                    // 같은 클립을 여러 번 끼워도 되도록 조각마다 입력을 따로 연다.
                    int idx = inputs.Count;
                    inputs.Add($"-i \"{it.File}\"");
                    BundledFfmpeg.MediaInfo ci = clipInfo[it.File];
                    // 클립 안 삭제를 뺀 조각만, 조각의 배속으로. 소리도 같은 구간을 잘라 같은 배속으로(스템은 클립과 같은 시간축).
                    string clipTrim = $"atrim=start={F(it.ClipStart)}:end={F(it.ClipEnd)},asetpts=PTS-STARTPTS{Atempo(it.Rate)},";
                    f.Append($"[{idx}:v]trim=start={F(it.ClipStart)}:end={F(it.ClipEnd)},setpts=(PTS-STARTPTS)/{F(it.Rate)}[t{k}];");
                    f.Append(FrameFilter($"[t{k}]", ci.Width, ci.Height, outW, outH, framing.Crop, framing.Focus, $"[f{k}]", k.ToString()) + ";");
                    f.Append($"[f{k}]fps={fps},format=yuv420p[v{k}];");
                    string dur = F(len);
                    bool muteBgm = it.MuteBgm;
                    // 클립 안 음소거는 자르기 전에(클립 안 시각 그대로) 건다. BGM/SFX 만 끄는 구간은 스템이 있어야 따로 된다.
                    bool partMute = it.Mutes != null && it.Mutes.Any(m => m.Target != MuteTarget.All);
                    const string Fmt48 = "aformat=sample_rates=48000:channel_layouts=stereo";
                    if ((muteBgm || it.MuteSfx || partMute) && EditList.ClipStems(it.File, out string cb, out string cs))
                    {
                        // Locale Capture 녹화: 클립 소리 대신 스템을 쓰고, 끈 쪽은 뺀다.
                        var pads = new List<string>();
                        if (cb != null && !muteBgm) { inputs.Add($"-i \"{cb}\""); f.Append($"[{inputs.Count - 1}:a]{Fmt48}{MuteFilter(it.Mutes, MuteTarget.Bgm)},{clipTrim}apad,atrim=end={dur},asetpts=PTS-STARTPTS[cb{k}];"); pads.Add($"[cb{k}]"); }
                        if (cs != null && !it.MuteSfx) { inputs.Add($"-i \"{cs}\""); f.Append($"[{inputs.Count - 1}:a]{Fmt48}{MuteFilter(it.Mutes, MuteTarget.Sfx)},{clipTrim}apad,atrim=end={dur},asetpts=PTS-STARTPTS[cs{k}];"); pads.Add($"[cs{k}]"); }
                        if (pads.Count == 0)
                        {
                            inputs.Add($"-f lavfi -t {dur} -i anullsrc=r=48000:cl=stereo");
                            f.Append($"[{inputs.Count - 1}:a]anull[a{k}];");
                        }
                        else if (pads.Count == 1) f.Append($"{pads[0]}anull[a{k}];");
                        else f.Append($"{pads[0]}{pads[1]}amix=inputs=2:duration=longest:normalize=0[a{k}];");
                    }
                    else if (clipInfo[it.File].HasAudio)
                        f.Append($"[{idx}:a]{Fmt48}{MuteFilter(it.Mutes, MuteTarget.All, true)},{clipTrim}apad,atrim=end={dur},asetpts=PTS-STARTPTS[a{k}];");
                    else
                    {
                        inputs.Add($"-f lavfi -t {dur} -i anullsrc=r=48000:cl=stereo");
                        f.Append($"[{inputs.Count - 1}:a]anull[a{k}];");
                    }
                }
                concatPads.Append($"[v{k}][a{k}]");
            }
            f.Append($"{concatPads}concat=n={items.Count}:v=1:a=1[vc][a];");
            f.Append($"{OutputFadeChain(edits, items, lengths, total, outW, outH, fps, "[vc]", f)}null[v]");

            string args = $"-y -loglevel error -stats {string.Join(" ", inputs)} -filter_complex \"{f}\" -map \"[v]\" -map \"[a]\" " +
                          $"-r {fps} {EncoderArgs(encode)} \"{outputFile}\"";
            Directory.CreateDirectory(Path.GetDirectoryName(outputFile));
            return BundledFfmpeg.Run(args, line =>
            {
                float t = BundledFfmpeg.ParseProgress(line);
                if (t >= 0 && total > 0) progress?.Invoke(Math.Min(1f, t / total));
            });
        }

        // 영상 한 장면을 PNG 로. 비율·화면 맞춤은 내보내기와 같다.
        public static string Thumbnail(string video, float time, string outputFile, int frameWidth, int frameHeight, EditList.Framing framing)
        {
            BundledFfmpeg.MediaInfo info = BundledFfmpeg.Probe(video);
            if (!info.HasVideo) return "영상을 읽지 못했다: " + video;
            Directory.CreateDirectory(Path.GetDirectoryName(outputFile));
            string graph = FrameFilter("[0:v]", info.Width, info.Height, frameWidth, frameHeight, framing.Crop, framing.Focus, "[v]", "0");
            return BundledFfmpeg.Run($"-y -loglevel error -ss {F(Math.Max(0, time))} -i \"{video}\" -filter_complex \"{graph}\" -map \"[v]\" -frames:v 1 \"{outputFile}\"");
        }

        // 출력 화면 크기 맞추기. 비율이 같으면 크기만 바꾼다. 다르면 crop 이면 채워서 자르고(focus 위치),
        // 아니면 영상을 안에 맞춘 뒤 빈 곳을 흐린 확대 영상으로 채운다. 블러는 1/4 크기에서 걸고 다시 키워 계산을 줄인다.
        // tag 는 같은 그래프 안에서 중간 패드 이름이 겹치지 않게 붙인다.
        public static string FrameFilter(string input, int srcW, int srcH, int w, int h, bool crop, float focus, string output, string tag)
        {
            if (w <= 0 || h <= 0) return $"{input}null{output}";
            if (Math.Abs((float) srcW / srcH - (float) w / h) < 0.01f) return $"{input}scale={w}:{h},setsar=1{output}";
            if (crop)
            {
                float p = Math.Max(0, Math.Min(1, focus));
                return $"{input}scale={w}:{h}:force_original_aspect_ratio=increase,crop={w}:{h}:(iw-{w})*{F(p)}:(ih-{h})*{F(p)},setsar=1{output}";
            }
            int bw = Math.Max(2, w / 4 / 2 * 2), bh = Math.Max(2, h / 4 / 2 * 2);
            return $"{input}split[fb{tag}][ff{tag}];" +
                   $"[fb{tag}]scale={bw}:{bh}:force_original_aspect_ratio=increase,crop={bw}:{bh},gblur=sigma=8,eq=brightness=-0.08,scale={w}:{h}[bg{tag}];" +
                   $"[ff{tag}]scale={w}:{h}:force_original_aspect_ratio=decrease[fg{tag}];" +
                   $"[bg{tag}][fg{tag}]overlay=(W-w)/2:(H-h)/2,setsar=1{output}";
        }

        // atempo 는 한 번에 0.5 배까지만 느려진다. 더 느리면 여러 번 건다.
        internal static string Atempo(float rate)
        {
            if (Math.Abs(rate - 1f) < 0.001f) return "";
            var sb = new StringBuilder();
            float r = rate;
            while (r < 0.5f) { sb.Append(",atempo=0.5"); r /= 0.5f; }
            while (r > 100f) { sb.Append(",atempo=100"); r /= 100f; }
            sb.Append($",atempo={F(r)}");
            return sb.ToString();
        }

        // 해당 스템에 적용할 음소거 구간. All 은 모든 스템에, 스템이 없을 때(allOnly)는 모든 구간을 영상 소리에.
        private static string MuteFilter(EditList edits, MuteTarget target, bool allOnly = false) => MuteFilter(edits.Mutes, target, allOnly);

        private static string MuteFilter(IEnumerable<EditList.Mute> list, MuteTarget target, bool allOnly = false)
        {
            if (list == null) return "";
            IEnumerable<EditList.Mute> mutes = list.Where(m => allOnly || m.Target == MuteTarget.All || m.Target == target);
            string enable = string.Join("+", mutes.Select(m => $"between(t,{F(Math.Min(m.Start, m.End))},{F(Math.Max(m.Start, m.End))})"));
            return enable.Length == 0 ? "" : $",volume=0:enable='{enable}'";
        }

        // 원본 [0, duration] 에서 삭제 구간을 뺀 조각들 사이에 삽입 클립을 끼우고, 원본 조각은 배속 경계에서 나눈 순서 목록.
        private static List<Item> BuildTimeline(float duration, EditList edits, string locale)
        {
            var cuts = edits.Cuts.Select(c => (start: Math.Max(0, Math.Min(c.Start, c.End)), end: Math.Min(duration, Math.Max(c.Start, c.End))))
                .Where(c => c.end > c.start).OrderBy(c => c.start).ToList();
            var keep = new List<(float start, float end)>();
            float cursor = 0;
            foreach ((float start, float end) in cuts)
            {
                if (start > cursor) keep.Add((cursor, start));
                cursor = Math.Max(cursor, end);
            }
            if (cursor < duration) keep.Add((cursor, duration));

            var inserts = edits.Inserts.Where(i => !string.IsNullOrEmpty(i.FileFor(locale))).OrderBy(i => i.At).ToList();
            var items = new List<Item>();
            int next = 0;
            foreach ((float start, float end) in keep)
            {
                float s = start;
                while (next < inserts.Count && inserts[next].At <= end)
                {
                    float at = Math.Max(s, inserts[next].At);
                    if (at > s) AddSource(items, edits, s, at);
                    items.Add(ClipItem(inserts[next], locale, at));
                    s = at;
                    next++;
                }
                if (end > s) AddSource(items, edits, s, end);
            }
            for (; next < inserts.Count; next++) items.Add(ClipItem(inserts[next], locale, duration));

            return items;
        }

        private static Item ClipItem(EditList.Insert ins, string locale, float at)
            => new Item { IsClip = true, File = ins.FileFor(locale), MuteBgm = ins.MuteBgm, MuteSfx = ins.MuteSfx, Mutes = ins.Mutes, InsertId = ins.Id, At = at };

        // 페이드 끝점의 결과 영상 시각. 클립 안이면 그 클립의 조각들 중 해당 지점(지운 구간 안이면 다음 조각 시작),
        // 아니면 원본 조각은 지난 만큼(배속 반영), 클립은 그 자리 앞(start 면 같은 자리 포함)에 놓인 것만 더한다.
        private static float OutputTime(List<Item> items, float[] lengths, float source, string clip, float clipTime, bool start)
        {
            float acc = 0;
            if (!string.IsNullOrEmpty(clip))
            {
                bool seen = false;
                for (int k = 0; k < items.Count; k++)
                {
                    Item it = items[k];
                    if (it.IsClip && it.InsertId == clip)
                    {
                        seen = true;
                        if (clipTime < it.ClipStart) return acc;
                        if (clipTime <= it.ClipEnd) return acc + (clipTime - it.ClipStart) / it.Rate;
                    }
                    else if (seen) return acc;   // 클립 마지막 조각 뒤(끝부분을 지운 곳)
                    acc += lengths[k];
                }
                if (seen) return acc;
                // 클립을 전부 지웠으면 원본 시각(삽입 지점)으로
            }
            acc = 0;
            for (int k = 0; k < items.Count; k++)
            {
                Item it = items[k];
                if (it.IsClip) { if (start ? it.At <= source : it.At < source) acc += lengths[k]; }
                else acc += Math.Max(0, Math.Min(source - it.Start, it.End - it.Start)) / it.Rate;
            }
            return acc;
        }

        // 구간 페이드: 이어 붙인 결과 영상 위에 검은 판을 겹친다. 끝점을 결과 영상 시각으로 옮기므로, 구간 안의 삭제 부분은 빠지고
        // 배속·삽입 클립이 반영된 남은 길이에서 앞 절반은 덮이고(어두워짐) 뒤 절반은 걷힌다(밝아짐).
        // 검은 판의 alpha 를 fade=t=in 으로 0→1, fade=t=out 으로 1→0. 마지막 패드를 돌려준다.
        private static string OutputFadeChain(EditList edits, List<Item> items, float[] lengths, float total, int w, int h, int fps, string pad, StringBuilder f)
        {
            int n = 0;
            void Layer(string fade, float from, float to)
            {
                f.Append($"color=black:s={w}x{h}:r={fps}:d={F(total)},format=yuva420p,{fade}[ok{n}];");
                f.Append($"{pad}[ok{n}]overlay=eof_action=pass:enable='between(t,{F(from)},{F(to)})'[ov{n}];");
                pad = $"[ov{n}]";
                n++;
            }
            foreach (EditList.Fade fd in edits.Fades)
            {
                // 원본만 쓰는 옛 페이드는 Start > End 일 수 있다
                float s0 = fd.TouchesClip ? fd.Start : Math.Min(fd.Start, fd.End), s1 = fd.TouchesClip ? fd.End : Math.Max(fd.Start, fd.End);
                float a = OutputTime(items, lengths, s0, fd.StartClip, fd.StartClipTime, true);
                float b = OutputTime(items, lengths, s1, fd.EndClip, fd.EndClipTime, false);
                if (b < a) (a, b) = (b, a);
                a = Math.Max(0, a); b = Math.Min(total, b);
                float len = (b - a) / 2;
                if (len < 0.02f) continue;
                Layer($"fade=t=in:st={F(a)}:d={F(len)}:alpha=1", a, a + len);
                Layer($"fade=t=out:st={F(b - len)}:d={F(len)}:alpha=1", b - len, b);
            }
            return pad;
        }

        // 원본 구간을 배속 경계에서 나눠 넣는다.
        private static void AddSource(List<Item> items, EditList edits, float start, float end)
        {
            var cuts = new SortedSet<float> { start, end };
            foreach (EditList.Speed sp in edits.Speeds)
            {
                float a = Math.Min(sp.Start, sp.End), b = Math.Max(sp.Start, sp.End);
                if (a > start && a < end) cuts.Add(a);
                if (b > start && b < end) cuts.Add(b);
            }
            float prev = float.NaN;
            foreach (float t in cuts)
            {
                if (!float.IsNaN(prev) && t - prev > 0.0001f)
                    items.Add(new Item { Start = prev, End = t, Rate = edits.RateAt((prev + t) / 2) });
                prev = t;
            }
        }
    }
}
