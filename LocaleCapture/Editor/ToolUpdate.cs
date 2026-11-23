using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace CookApps.LocaleCapture.Editor
{
    // 도구 창의 업데이트. 배포본을 받아 설치본과 비교하는 일은 백그라운드에서 하고(UpdateCore), 다르면 물어본 뒤 바꾼다.
    //  · 창을 열 때 조용히 확인한다(6시간에 한 번까지) — 이때는 로그인 창을 띄우지 않는다.
    //  · 녹화·재생·플레이 중에는 확인도 설치도 하지 않는다.
    internal static class ToolUpdate
    {
        public enum State { Unknown, Checking, Latest, Available, Downloading, Failed }

        [Serializable]
        private sealed class Saved
        {
            public State State;
            public string Current = "", Latest = "", Problem = "", Detail = "";
            public int Files;
            public long CheckedAt;
            public string Install = "";   // 확인했을 때의 설치 폴더 지문 — 그 뒤 설치본이 바뀌면 이 결과는 버린다(DropIfInstallChanged)
        }

        private sealed class Result
        {
            public UpdateCore.Outcome Outcome;
            public UpdateCore.Release Release, Installed;
            public List<UpdateCore.Change> Changes;
            public List<string> Log = new List<string>();
            public string Fingerprint = "";   // 비교한 설치 폴더의 지문
        }

        private const string Key = "LocaleCapture.Update", Title = "Locale Capture 업데이트";
        private const double AutoHours = 6;

        private static Saved _saved;
        private static Task<Result> _check;
        private static Task<UpdateCore.Outcome> _download;
        private static Result _found;
        private static bool _manual;   // 손으로 누른 확인인가(다르면 바로 묻고, 실패하면 창으로 알린다)

        // 마지막 확인 결과. 스크립트가 다시 로드돼도 남는다(에디터를 끄면 사라진다).
        private static Saved Now
        {
            get
            {
                if (_saved != null) return _saved;
                string json = SessionState.GetString(Key, "");
                _saved = (string.IsNullOrEmpty(json) ? null : JsonUtility.FromJson<Saved>(json)) ?? new Saved();
                // 확인·받기 도중에 스크립트가 다시 로드됐으면 그 작업은 사라졌다.
                if (_saved.State == State.Checking || _saved.State == State.Downloading)
                {
                    _saved.State = State.Unknown;
                    _saved.CheckedAt = 0;
                }
                return _saved;
            }
        }

        private static void Set(State state, Action<Saved> change = null)
        {
            Saved saved = Now;
            saved.State = state;
            change?.Invoke(saved);
            SessionState.SetString(Key, JsonUtility.ToJson(saved));
        }

        public static State Status => Now.State;

        public static bool Working => _check != null || _download != null;

        public static bool CanRun => !CaptureDirector.Busy && !EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isCompiling;

        // 창 맨 아래 Version 줄.
        public static string Summary
        {
            get
            {
                Saved s = Now;
                switch (s.State)
                {
                    case State.Checking: return Join(s.Current, "확인 중…");
                    case State.Downloading: return Join(s.Current, "받는 중…");
                    case State.Latest: return Join(s.Current, "최신");
                    case State.Available: return Join(s.Current, "새 버전 있음");
                    case State.Failed: return Join(s.Current, "확인 못 함: " + s.Problem);
                    default: return string.IsNullOrEmpty(s.Current) ? "확인 전" : s.Current;
                }
            }
        }

        // Version 줄에 마우스를 올리면 보이는 자세한 내용(바뀐 내용, 실패 까닭).
        public static string Detail => Now.Detail;

        // 새 버전이 있을 때 상태 줄 아래에 띄우는 안내.
        public static string Notice
        {
            get
            {
                Saved s = Now;
                // 패키지로 직접 깐 뒤 처음 확인할 때는 설치된 버전을 모른다(배포본과 다르다는 것만 안다).
                string from = string.IsNullOrEmpty(s.Current) ? "설치된 버전은 알 수 없다(배포본과 다르다)" : $"설치된 버전 {s.Current}";
                return $"새 버전이 있다 — {s.Latest}\n{from} · 바뀌는 파일 {s.Files}개";
            }
        }

        private static string Join(string current, string status) => string.IsNullOrEmpty(current) ? status : $"{current} — {status}";

        // 받아 두는 곳: 프로젝트 밖(이 PC 의 사용자 폴더). 어느 프로젝트에서 열어도 같은 것을 쓴다.
        private static string CacheDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CookApps", "LocaleCapture", "update");

        private static string StampFile => Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Library", "LocaleCapture", "installed.txt");

        // 설치 폴더(Editor·Runtime 이 든 곳). Assets 아래에 깔린 경우만 여기서 바꾼다.
        private static bool Locate(out string install, out string why)
        {
            install = null;
            why = null;
            string asmdef = UnityEditor.Compilation.CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(typeof(ToolUpdate).Assembly.GetName().Name);
            if (string.IsNullOrEmpty(asmdef)) { why = "설치 폴더를 찾지 못했다"; return false; }
            asmdef = asmdef.Replace('\\', '/');
            if (!asmdef.StartsWith("Assets/", StringComparison.Ordinal)) { why = "Assets 아래에 깔린 경우만 여기서 업데이트한다"; return false; }
            string folder = Path.GetDirectoryName(Path.GetDirectoryName(asmdef));
            install = Path.GetFullPath(Path.Combine(Directory.GetParent(Application.dataPath).FullName, folder));
            if (!UpdateCore.LooksInstalled(install)) { why = $"설치 폴더가 아니다: {folder}"; return false; }
            return true;
        }

        // 창을 열 때: 아직 안 봤거나 본 지 오래됐으면 조용히 확인한다.
        public static void AutoCheck()
        {
            if (Working) return;
            DropIfInstallChanged();
            if (!CanRun) return;
            long at = Now.CheckedAt;
            if (at > 0 && (DateTime.UtcNow - new DateTime(at, DateTimeKind.Utc)).TotalHours < AutoHours) return;
            Check(false);
        }

        // 기억해 둔 확인 결과("최신"·"새 버전 있음")는 그때의 설치본을 본 것이다. 그 뒤 설치 폴더가 바뀌었으면(패키지를 다시 가져옴,
        // 파일을 직접 고침) 버려서 곧바로 다시 확인하게 한다 — 안 그러면 최신 패키지를 가져온 직후에도 예전의 "새 버전이 있다" 가
        // 다음 자동 확인(6시간 뒤)까지 그대로 보인다. 지문은 파일 이름·크기·고친 시각만 보므로 창을 열 때마다 해도 가볍다.
        private static void DropIfInstallChanged()
        {
            try
            {
                Saved saved = Now;
                if (saved.State != State.Available && saved.State != State.Latest) return;
                if (!Locate(out string install, out _)) return;
                if (UpdateCore.Fingerprint(install) == saved.Install) return;
                _found = null;
                Set(State.Unknown, s => { s.Current = ""; s.Latest = ""; s.Files = 0; s.Problem = ""; s.Detail = ""; s.CheckedAt = 0; s.Install = ""; });
            }
            catch { /* 지문을 못 읽으면 기억한 결과를 그대로 둔다 — 다음 확인이 바로잡는다 */ }
        }

        private static string FingerprintOf(string install)
        {
            try { return UpdateCore.Fingerprint(install); }
            catch { return ""; }
        }

        // 창의 버튼: 새로 받아 비교하고, 다르면 바로 물어본다(전에 찾아 둔 결과는 쓰지 않는다 — 배포본도 설치본도 그새 바뀌었을 수 있다).
        public static void Run()
        {
            if (Working || !CanRun) return;
            Check(true);
        }

        private static void Check(bool manual)
        {
            _manual = manual;
            _found = null;
            Set(State.Checking, s => s.CheckedAt = DateTime.UtcNow.Ticks);
            if (!Locate(out string install, out string why)) { Fail(UpdateCore.Problem.Other, why); return; }
            string cache = CacheDir, stamp = StampFile;
            _check = Task.Run(() => Compare(cache, install, stamp, manual));
            EditorApplication.update -= Pump;
            EditorApplication.update += Pump;
        }

        // 백그라운드: Unity 를 부르지 않는다.
        private static Result Compare(string cache, string install, string stamp, bool interactive)
        {
            // 지문은 비교하기 전에 뜬다 — 비교하는 사이 파일이 바뀌면 다음에 창을 열 때 다시 확인하게 된다(안전한 쪽).
            var result = new Result { Installed = UpdateCore.ReadStamp(stamp, install), Fingerprint = FingerprintOf(install) };
            result.Outcome = UpdateCore.Fetch(cache, UpdateCore.Branch, interactive, out result.Release);
            if (!result.Outcome.Ok) return result;
            result.Changes = UpdateCore.Diff(cache, install, out string error);
            if (result.Changes == null)
            {
                result.Outcome = new UpdateCore.Outcome { Problem = UpdateCore.Problem.Other, Detail = error };
                return result;
            }
            if (result.Changes.Count == 0)
            {
                UpdateCore.WriteStamp(stamp, result.Release, install);
                result.Installed = result.Release;
            }
            else result.Log = UpdateCore.Log(cache, result.Installed?.Sha, result.Release.Sha);
            return result;
        }

        private static void Pump()
        {
            if (_check != null && _check.IsCompleted)
            {
                Task<Result> task = _check;
                _check = null;
                if (task.IsFaulted) Fail(UpdateCore.Problem.Other, task.Exception?.GetBaseException().Message);
                else Checked(task.Result);
            }
            if (_download != null && _download.IsCompleted)
            {
                Task<UpdateCore.Outcome> task = _download;
                _download = null;
                if (task.IsFaulted) Fail(UpdateCore.Problem.Other, task.Exception?.GetBaseException().Message);
                else if (!task.Result.Ok) Fail(task.Result.Problem, task.Result.Detail);
                else Install();
            }
            if (!Working) EditorApplication.update -= Pump;
        }

        private static void Checked(Result result)
        {
            if (!result.Outcome.Ok) { Fail(result.Outcome.Problem, result.Outcome.Detail); return; }
            string current = result.Installed?.Label ?? "";
            if (result.Changes.Count == 0)
            {
                Set(State.Latest, s => { s.Current = current; s.Latest = current; s.Files = 0; s.Problem = ""; s.Detail = result.Release.Subject; s.Install = result.Fingerprint; });
                return;
            }
            _found = result;
            Set(State.Available, s =>
            {
                s.Current = current; s.Latest = result.Release.Label; s.Files = result.Changes.Count; s.Problem = ""; s.Detail = string.Join("\n", Lines(result));
                s.Install = result.Fingerprint;
            });
            if (_manual) Confirm();
        }

        private static List<string> Lines(Result result) => result.Log.Count > 0 ? result.Log : new List<string> { result.Release.Subject };

        // 창을 열 때의 자동 확인은 Version 줄에만 남기고, 손으로 눌렀을 때는 무엇을 하면 되는지 창으로 알린다.
        private static void Fail(UpdateCore.Problem problem, string detail)
        {
            Set(State.Failed, s => { s.Problem = Short(problem); s.Detail = detail ?? ""; });
            if (!_manual) return;
            Debug.LogWarning($"[LocaleCapture] 업데이트: {Short(problem)}" + (string.IsNullOrEmpty(detail) ? "" : "\n" + detail));
            EditorUtility.DisplayDialog(Title, Help(problem) + (string.IsNullOrEmpty(detail) ? "" : $"\n\n({detail})"), "확인");
        }

        private static string Short(UpdateCore.Problem problem)
        {
            switch (problem)
            {
                case UpdateCore.Problem.NoGit: return "git 이 없다";
                case UpdateCore.Problem.NoAccess: return "저장소 로그인·권한 필요";
                case UpdateCore.Problem.NoNetwork: return "연결 안 됨";
                case UpdateCore.Problem.NoRelease: return "배포된 버전이 없다";
                case UpdateCore.Problem.NoLfs: return "Git LFS 가 없다";
                default: return "오류";
            }
        }

        private static string Help(UpdateCore.Problem problem)
        {
            switch (problem)
            {
                case UpdateCore.Problem.NoGit: return "git 을 찾지 못했다.\nGit 을 설치한 뒤 Unity 를 다시 열고 눌러 본다.";
                case UpdateCore.Problem.NoAccess:
                    return $"저장소에 접근하지 못했다.\n깃허브 로그인 창이 뜨면 로그인하고, 계정에 저장소 읽기 권한이 있는지 관리자에게 확인한다.\n{UpdateCore.Repository}";
                case UpdateCore.Problem.NoNetwork: return "저장소에 연결하지 못했다.\n네트워크를 확인하고 다시 눌러 본다.";
                case UpdateCore.Problem.NoRelease: return "배포된 버전이 아직 없다.";
                case UpdateCore.Problem.NoLfs: return "Git LFS 가 없어 큰 파일을 받지 못했다.\nGit 을 다시 설치하면서 Git LFS 를 포함시킨 뒤 눌러 본다.";
                default: return "업데이트하지 못했다.";
            }
        }

        // 바뀌는 내용을 보여 주고, 좋다고 하면 (큰 파일이 바뀌었으면 받은 뒤) 설치한다.
        private static void Confirm()
        {
            Result found = _found;
            if (found == null || !CanRun) return;
            int big = found.Changes.Count(c => c.Lfs);
            long bytes = found.Changes.Where(c => c.Lfs).Sum(c => c.Size);
            int removed = found.Changes.Count(c => c.Delete);
            bool known = found.Installed != null;
            string text = $"새 버전 {found.Release.Label}\n\n{(found.Log.Count > 0 ? "바뀐 내용" : "최근 바뀐 내용")}\n- " + string.Join("\n- ", Lines(found).Take(10))
                + $"\n\n파일 {found.Changes.Count}개를 바꾼다" + (removed > 0 ? $"(그중 {removed}개는 삭제)" : "")
                + (big > 0 ? $". 큰 파일 {big}개({bytes / 1048576f:F0}MB)를 먼저 받는다" : "")
                + (known ? "" : ". 설치 폴더에서 직접 고친 파일이 있으면 배포본으로 돌아간다")
                + ". 끝나면 스크립트가 다시 컴파일된다.";
            if (!EditorUtility.DisplayDialog(Title, text, "업데이트", "나중에")) return;
            if (big == 0) { Install(); return; }
            string cache = CacheDir, sha = found.Release.Sha;
            List<UpdateCore.Change> changes = found.Changes;
            Set(State.Downloading);
            _download = Task.Run(() => UpdateCore.FetchLfs(cache, sha, changes, true));
            EditorApplication.update -= Pump;
            EditorApplication.update += Pump;
        }

        private static void Install()
        {
            Result found = _found;
            if (found == null) { Fail(UpdateCore.Problem.Other, "설치할 내용이 없다 — 다시 확인한다"); return; }
            if (!CanRun)
            {
                Set(State.Available);
                Debug.LogWarning("[LocaleCapture] 업데이트: 녹화·플레이 중이라 설치하지 않았다. 끝난 뒤 다시 누른다.");
                return;
            }
            if (!Locate(out string install, out string why)) { Fail(UpdateCore.Problem.Other, why); return; }
            string cache = CacheDir;
            try
            {
                EditorUtility.DisplayProgressBar(Title, $"파일 {found.Changes.Count}개를 바꾸는 중", 0.5f);
                UpdateCore.Apply(cache, install, found.Changes);
                // 바꾼 뒤 다시 비교한다 — 하나라도 남았으면 끝났다고 하지 않는다.
                List<UpdateCore.Change> left = UpdateCore.Diff(cache, install, out string error);
                if (left == null) throw new IOException(error);
                if (left.Count > 0) throw new IOException($"바뀌지 않은 파일이 {left.Count}개 남았다: {string.Join(", ", left.Take(5).Select(c => c.Path))}");
                UpdateCore.WriteStamp(StampFile, found.Release, install);
                UpdateCore.Trim(cache);
                string label = found.Release.Label, print = FingerprintOf(install);   // 바꾼 뒤의 설치본
                Set(State.Latest, s => { s.Current = label; s.Latest = label; s.Files = 0; s.Problem = ""; s.Detail = found.Release.Subject; s.Install = print; });
                Debug.Log($"[LocaleCapture] 업데이트 완료 — {label}, 파일 {found.Changes.Count}개\n- " + string.Join("\n- ", found.Changes.Select(c => (c.Delete ? "삭제 " : "") + c.Path)));
                _found = null;
            }
            catch (Exception e)
            {
                const string partial = "일부 파일만 바뀌었을 수 있다. 업데이트를 다시 누르면 남은 것을 마저 바꾼다.";
                Set(State.Failed, s => { s.Problem = "설치하지 못했다"; s.Detail = e.Message; });
                Debug.LogError($"[LocaleCapture] 업데이트 실패 — {e.Message}\n{partial}");
                EditorUtility.ClearProgressBar();
                EditorUtility.DisplayDialog(Title, $"설치하지 못했다.\n{e.Message}\n\n{partial}", "확인");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                AssetDatabase.Refresh();
            }
        }
    }
}
