using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;

record State(string LastUrl, string? LastVersion, DateTime LastSeenUtc);

class Program
{
    // 1차 타겟: 실제 패치노트 인덱스(ko-kr)
    static readonly Uri IndexUri = new("https://info.monsterhunter.com/wilds/update/ko-kr/");
    // 보조 타겟(원한다면 추후 메인 허브 페이지도 감시 가능)
    // static readonly Uri HubUri = new("https://www.monsterhunter.com/wilds/ko-kr/update/");

    // 기존: AppContext.BaseDirectory, "last_seen.json"  (bin쪽)
    // 변경: 프로젝트 폴더(MHWsUpdateWatcher/last_seen.json) 타겟
    // static readonly string StateFile = Path.Combine(AppContext.BaseDirectory, "last_seen.json");
    static readonly string StateFile = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "last_seen.json"));
    static readonly Regex VerRegex = new(@"Ver\.(\d+(?:\.\d+)+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static async Task Main()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        
        var webhook = Environment.GetEnvironmentVariable("DISCORD_WEBHOOK_URL");
        if (string.IsNullOrWhiteSpace(webhook))
        {
            Console.Error.WriteLine("환경변수 DISCORD_WEBHOOK_URL이 설정되어야 합니다.");
            Environment.Exit(1);
            return;
        }

        using var http = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true
        })
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome Safari");
        http.DefaultRequestHeaders.AcceptLanguage.Add(new StringWithQualityHeaderValue("ko-KR"));
        http.DefaultRequestHeaders.AcceptLanguage.Add(new StringWithQualityHeaderValue("ko", 0.9));
        http.DefaultRequestHeaders.AcceptLanguage.Add(new StringWithQualityHeaderValue("en", 0.8));

        try
        {
            var latest = await FetchLatestAsync(http, IndexUri);
            if (latest == null)
            {
                Console.WriteLine("최신 항목을 찾지 못했습니다. 페이지 구조가 바뀌었을 수 있습니다.");
                return;
            }

            var (latestUrl, latestVer, latestText) = latest.Value;

            var prev = LoadState();
            if (prev == null || !string.Equals(prev.LastUrl, latestUrl, StringComparison.Ordinal))
            {
                // 새 업데이트 발견!
                await NotifyDiscordAsync(http, webhook, latestUrl, latestVer, latestText);
                SaveState(new State(latestUrl, latestVer, DateTime.UtcNow));
                Console.WriteLine($"Notified new update: {latestUrl}");
            }
            else
            {
                Console.WriteLine("변경 없음.");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("오류: " + ex.Message);
            // Actions가 계속 실패로 뜨지 않도록 0 종료를 원하면 return; 사용
            Environment.ExitCode = 1;
        }
    }

    static State? LoadState()
    {
        if (!File.Exists(StateFile)) return null;
        try
        {
            var json = File.ReadAllText(StateFile);
            return JsonSerializer.Deserialize<State>(json);
        }
        catch { return null; }
    }

    static void SaveState(State s)
    {
        var json = JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(StateFile, json, Encoding.UTF8);
    }

    static async Task<(string url, string? ver, string text)?> FetchLatestAsync(HttpClient http, Uri baseUri)
    {
        var html = await http.GetStringAsync(baseUri);
        var config = Configuration.Default;
        var context = BrowsingContext.New(config);
        var doc = await context.OpenAsync(req => req.Content(html).Address(baseUri.ToString()));

        var candidates = new List<(string url, string? ver, string text)>();

        foreach (var a in doc.QuerySelectorAll("a[href]"))
        {
            var href = a.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(href)) continue;

            var abs = new Uri(baseUri, href).ToString();
            var text = a.TextContent?.Trim() ?? "";

            // 링크/텍스트에 Ver.가 들어간 항목만 후보로
            var m = VerRegex.Match(href) is { Success: true } mh ? mh :
                    VerRegex.Match(text) is { Success: true } mt ? mt : null;

            if (m != null)
            {
                var ver = m.Groups[1].Value; // "1.021.01.00" 같은 문자열
                candidates.Add((abs, ver, string.IsNullOrWhiteSpace(text) ? abs : text));
            }
        }

        if (candidates.Count == 0) return null;

        // 버전 문자열을 정렬해서 가장 최신 선택
        candidates.Sort((a, b) => CompareVer(b.ver, a.ver)); // 내림차순
        return candidates[0];
    }

    static int CompareVer(string? a, string? b)
    {
        if (a == b) return 0;
        if (a is null) return -1;
        if (b is null) return 1;

        static IEnumerable<int> Parts(string s)
            => s.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(x => int.TryParse(x, out var n) ? n : 0);

        var pa = Parts(a).ToArray();
        var pb = Parts(b).ToArray();
        var len = Math.Max(pa.Length, pb.Length);
        for (int i = 0; i < len; i++)
        {
            var ai = i < pa.Length ? pa[i] : 0;
            var bi = i < pb.Length ? pb[i] : 0;
            var cmp = ai.CompareTo(bi);
            if (cmp != 0) return cmp;
        }
        return 0;
    }

    static async Task NotifyDiscordAsync(HttpClient http, string webhook, string latestUrl, string? latestVer, string title)
    {
        // 간단: content로 전송 (임베드로 예쁘게 보내려면 embeds 필드 사용)
        var contentText =
            $"**몬스터헌터 와일즈 업데이트 감지!**\n" +
            $"{(string.IsNullOrWhiteSpace(latestVer) ? "" : $"버전: {latestVer}\n")}" +
            $"{title}\n{latestUrl}";

        var payload = new
        {
            content = contentText
            // embeds = new[] { new { title = "몬스터헌터 와일즈 업데이트 감지", description = title, url = latestUrl } }
        };

        var json = JsonSerializer.Serialize(payload);
        using var req = new HttpRequestMessage(HttpMethod.Post, webhook)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        var res = await http.SendAsync(req);
        res.EnsureSuccessStatusCode();
    }
}
