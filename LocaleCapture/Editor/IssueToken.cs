using System;
using System.Text;

namespace CookApps.LocaleCapture.Editor
{
    // 이슈 전송용 GitHub 토큰. 사내 배포 전제로 패키지에 같이 들어간다 — 이 저장소 하나에 Issues 쓰기 권한만 있는
    // fine-grained 토큰을 만들어 base64 로 적는다(평문 토큰 패턴 검사에 걸리지 않게). 비어 있으면 브라우저의 새 이슈 페이지로 대신한다.
    //   토큰 만들기: GitHub → Settings → Developer settings → Fine-grained tokens → Repository access: locale_capture 만,
    //   Permissions: Issues Read and write. 만료는 1년.  base64: PowerShell  [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("github_pat_..."))
    internal static class IssueToken
    {
        private const string Base64 = "";

        public static string Value
        {
            get
            {
                try { return string.IsNullOrEmpty(Base64) ? "" : Encoding.UTF8.GetString(Convert.FromBase64String(Base64)).Trim(); }
                catch { return ""; }
            }
        }
    }
}
