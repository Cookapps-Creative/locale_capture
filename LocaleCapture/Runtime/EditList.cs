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
            public float At;                 // 원본의 이 시각 앞에 끼운다
            public string File;              // 모든 언어 공통 클립
            // 클립의 BGM·SFX 끄기. Locale Capture 녹화(폴더에 스템이 있는 영상)만 가능하다.
            public bool MuteBgm, MuteSfx;
            public List<string> LocaleKeys = new List<string>();    // 언어별로 다른 클립이 있으면 여기
            public List<string> LocaleFiles = new List<string>();

            public string FileFor(string locale)
            {
                int i = LocaleKeys.IndexOf(locale);
                return i >= 0 && !string.IsNullOrEmpty(LocaleFiles[i]) ? LocaleFiles[i] : File;
            }
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
        public List<Range> Fades = new List<Range>();     // 구간 앞 절반은 검은 화면에서 밝아지고, 뒤 절반은 다시 어두워진다
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
            Fades ??= new List<Range>();
            Framings ??= new List<Framing>();
            return this;
        }
    }
}
