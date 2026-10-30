using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace CookApps.LocaleCapture
{
    public enum MuteTarget { All, Bgm, Sfx }

    // 한 기록에 대한 편집 지시. 시간은 전부 원본 영상 기준 초이며, 모든 언어 영상에 똑같이 적용된다.
    [Serializable]
    public sealed class EditList
    {
        [Serializable]
        public struct Range
        {
            public float Start, End;
        }

        [Serializable]
        public struct Mute
        {
            public float Start, End;
            public MuteTarget Target;   // BGM·SFX 를 따로 끄려면 녹화 때 만든 스템(audio_bgm.wav·audio_sfx.wav)이 있어야 한다
        }

        [Serializable]
        public class Insert
        {
            public string Id;                // 페이드가 클립 안 위치를 가리킬 때 쓰는 고유 값
            public float At;                 // 원본의 이 시각 앞에 끼운다
            public string File;              // 모든 언어 공통 클립
            // 클립의 BGM·SFX 끄기. Locale Capture 녹화(폴더에 스템이 있는 영상)만 가능하다.
            public bool MuteBgm, MuteSfx;
            public List<string> LocaleKeys = new List<string>();    // 언어별로 다른 클립이 있으면 여기
            public List<string> LocaleFiles = new List<string>();
            public List<Range> Cuts = new List<Range>();            // 클립 안에서 지우는 구간(클립 안 시각, 초)
            public List<Speed> Speeds = new List<Speed>();          // 클립 안 배속 구간(클립 안 시각, 초)
            // 클립 안 음소거 구간(클립 안 시각, 초). BGM/SFX 만 끄려면 스템이 있어야 하고, 없으면 전체 음소거로 적용된다.
            public List<Mute> Mutes = new List<Mute>();

            // 클립 안 시각 t 의 재생 속도. 겹치면 나중에 넣은 구간이 이긴다.
            public float RateAt(float t)
            {
                if (Speeds != null)
                    for (int i = Speeds.Count - 1; i >= 0; i--)
                    {
                        Speed s = Speeds[i];
                        if (t >= Mathf.Min(s.Start, s.End) && t < Mathf.Max(s.Start, s.End) && s.Rate > 0) return s.Rate;
                    }
                return 1f;
            }

            // 클립 안 [start, end] 를 배속 경계에서 나눈 조각들(조각마다 배속 하나).
            public List<(float start, float end, float rate)> SpeedPieces(float start, float end)
            {
                var bounds = new SortedSet<float> { start, end };
                if (Speeds != null)
                    foreach (Speed s in Speeds)
                    {
                        float a = Mathf.Min(s.Start, s.End), b = Mathf.Max(s.Start, s.End);
                        if (a > start && a < end) bounds.Add(a);
                        if (b > start && b < end) bounds.Add(b);
                    }
                var list = new List<(float, float, float)>();
                float prev = float.NaN;
                foreach (float t in bounds)
                {
                    if (!float.IsNaN(prev) && t - prev > 0.0001f) list.Add((prev, t, RateAt((prev + t) / 2)));
                    prev = t;
                }
                return list;
            }

            // 클립 안 시각 t 를 둘러싼, 배속이 바뀌지 않는 칸.
            public (float start, float end) RateSpan(float t, float length)
            {
                float lo = 0, hi = length;
                if (Speeds != null)
                    foreach (Speed s in Speeds)
                        foreach (float b in new[] { Mathf.Min(s.Start, s.End), Mathf.Max(s.Start, s.End) })
                        {
                            if (b <= t && b > lo) lo = b;
                            if (b > t && b < hi) hi = b;
                        }
                return (lo, hi);
            }

            public string FileFor(string locale)
            {
                int i = LocaleKeys.IndexOf(locale);
                return i >= 0 && !string.IsNullOrEmpty(LocaleFiles[i]) ? LocaleFiles[i] : File;
            }

            // 클립 길이 length 에서 지우는 구간을 뺀 남는 조각들(클립 안 시각, 순서대로).
            public List<(float start, float end)> Keep(float length)
            {
                var cuts = new List<(float a, float b)>();
                if (Cuts != null)
                    foreach (Range c in Cuts)
                    {
                        float a = Mathf.Clamp(Mathf.Min(c.Start, c.End), 0, length), b = Mathf.Clamp(Mathf.Max(c.Start, c.End), 0, length);
                        if (b > a) cuts.Add((a, b));
                    }
                cuts.Sort((p, q) => p.a.CompareTo(q.a));
                var keep = new List<(float, float)>();
                float cur = 0;
                foreach ((float a, float b) in cuts)
                {
                    if (a > cur) keep.Add((cur, a));
                    cur = Mathf.Max(cur, b);
                }
                if (cur < length) keep.Add((cur, length));
                return keep;
            }

            // 클립 안 시각 t 가 지운 구간 안이면 다음 남는 조각의 시작으로(없으면 length).
            public float NextKept(float t, float length)
            {
                foreach ((float start, float end) in Keep(length))
                    if (t < end - 0.0001f) return Mathf.Max(t, start);
                return length;
            }
        }

        // 구간 페이드. Start·End 는 원본 시각. 끝점이 삽입 클립 안이면 StartClip/EndClip 에 그 클립 Id 와
        // 클립 안 시각을 두고, Start/End 는 그 클립의 삽입 지점(클립이 지워졌을 때 대신 쓰는 값)이다.
        [Serializable]
        public struct Fade
        {
            public float Start, End;
            public string StartClip, EndClip;
            public float StartClipTime, EndClipTime;

            public bool TouchesClip => !string.IsNullOrEmpty(StartClip) || !string.IsNullOrEmpty(EndClip);
        }

        [Serializable]
        public struct Speed
        {
            public float Start, End;
            public float Rate;          // 2 = 두 배 빠르게, 0.5 = 절반 속도
        }

        // 내보낼 비율별 화면 맞춤. Crop 이 아니면 안에 맞추고 빈 곳을 블러로 채운다.
        [Serializable]
        public struct Framing
        {
            public string Ratio;        // "9:16" 등
            public bool Crop;
            public float Focus;         // 자를 때 위치. 0 = 왼쪽/위, 1 = 오른쪽/아래
        }

        public List<Range> Cuts = new List<Range>();     // 지우는 구간
        public List<Mute> Mutes = new List<Mute>();      // 소리를 없애는 구간
        public List<Insert> Inserts = new List<Insert>();
        public List<Speed> Speeds = new List<Speed>();
        public List<Fade> Fades = new List<Fade>();       // 구간 앞 절반은 검게 어두워지고, 뒤 절반은 다시 밝아진다
        public List<Framing> Framings = new List<Framing>();

        // 목표 길이(초). 0 이면 없음. 타임라인 표시용.
        public float TargetLength;

        public const string FileName = "edit.json";
        public const string BgmStem = "audio_bgm.wav";
        public const string SfxStem = "audio_sfx.wav";

        public bool IsEmpty => Cuts.Count == 0 && Mutes.Count == 0 && Inserts.Count == 0 && Speeds.Count == 0 && Fades.Count == 0;

        // 원본 시각 t 의 재생 속도. 겹치면 나중에 넣은 구간이 이긴다.
        public float RateAt(float t)
        {
            for (int i = Speeds.Count - 1; i >= 0; i--)
            {
                Speed s = Speeds[i];
                if (t >= Mathf.Min(s.Start, s.End) && t < Mathf.Max(s.Start, s.End) && s.Rate > 0) return s.Rate;
            }
            return 1f;
        }

        public Framing FramingFor(string ratio)
        {
            foreach (Framing f in Framings) if (f.Ratio == ratio) return f;
            return new Framing { Ratio = ratio, Focus = 0.5f };
        }

        public void SetFraming(Framing framing)
        {
            Framings.RemoveAll(f => f.Ratio == framing.Ratio);
            Framings.Add(framing);
        }

        // 클립이 Locale Capture 녹화면 같은 폴더의 스템 경로를 준다(없으면 null). 소리는 언어와 무관해 폴더당 한 벌이다.
        public static bool ClipStems(string clipFile, out string bgm, out string sfx)
        {
            bgm = sfx = null;
            if (string.IsNullOrEmpty(clipFile)) return false;
            string dir = Path.GetDirectoryName(clipFile);
            if (string.IsNullOrEmpty(dir)) return false;
            string b = Path.Combine(dir, BgmStem), s = Path.Combine(dir, SfxStem);
            if (File.Exists(b)) bgm = b;
            if (File.Exists(s)) sfx = s;
            return bgm != null || sfx != null;
        }

        public void Save(string directory) => File.WriteAllText(Path.Combine(directory, FileName), JsonUtility.ToJson(this, true));

        public static EditList Load(string directory)
        {
            string path = Path.Combine(directory, FileName);
            EditList list = File.Exists(path) ? JsonUtility.FromJson<EditList>(File.ReadAllText(path)) : new EditList();
            return list.Normalized();
        }

        // 옛 edit.json 에 없던 필드를 채운다.
        public EditList Normalized()
        {
            Speeds ??= new List<Speed>();
            Fades ??= new List<Fade>();
            Framings ??= new List<Framing>();
            foreach (Insert ins in Inserts)
            {
                if (string.IsNullOrEmpty(ins.Id)) ins.Id = Guid.NewGuid().ToString("N");
                ins.Cuts ??= new List<Range>();
                ins.Speeds ??= new List<Speed>();
                ins.Mutes ??= new List<Mute>();
            }
            // 지워진 클립을 가리키는 끝점은 원본 시각(그 클립의 삽입 지점)으로 되돌린다.
            for (int i = 0; i < Fades.Count; i++)
            {
                Fade fd = Fades[i];
                if (!string.IsNullOrEmpty(fd.StartClip) && InsertById(fd.StartClip) == null) { fd.StartClip = null; fd.StartClipTime = 0; }
                if (!string.IsNullOrEmpty(fd.EndClip) && InsertById(fd.EndClip) == null) { fd.EndClip = null; fd.EndClipTime = 0; }
                Fades[i] = fd;
            }
            return this;
        }

        public Insert InsertById(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (Insert ins in Inserts) if (ins.Id == id) return ins;
            return null;
        }
    }
}
