using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace ChessArmy.LocEditor;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static int Main(string[] args)
    {
        var path = GamePaths.StringsCsv;

        // Contrôle de non-régression du round-trip (charge puis réécrit et compare octet à octet).
        if (args.Length >= 1 && args[0] == "--selftest")
        {
            var src = args.Length >= 2 ? args[1] : path;
            var tmp = src + ".roundtrip.tmp";
            LocFile.Load(src).Save(tmp);
            var same = File.ReadAllBytes(src).AsSpan().SequenceEqual(File.ReadAllBytes(tmp));
            Console.WriteLine(same ? $"ROUNDTRIP OK ({new FileInfo(src).Length} octets)" : "ROUNDTRIP DIFF");
            File.Delete(tmp);
            return same ? 0 : 1;
        }

        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"strings.csv introuvable : {path}");
            return 1;
        }

        var doc = LocFile.Load(path);
        var html = LoadHtml();

        var listener = BindFreePort(out var port);
        if (listener is null)
        {
            Console.Error.WriteLine("Aucun port local libre trouvé entre 5533 et 5543.");
            return 1;
        }
        var url = $"http://localhost:{port}/";

        Console.WriteLine($"Éditeur de traduction ouvert sur {url}");
        Console.WriteLine($"Fichier : {path}");
        Console.WriteLine("Ctrl+C pour arrêter le serveur.");
        if (!args.Contains("--no-open"))
            OpenBrowser(url);

        while (listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = listener.GetContext(); }
            catch { break; }
            try { Handle(ctx, doc, path, html); }
            catch (Exception ex)
            {
                try { WriteJson(ctx.Response, 500, new { error = ex.Message }); } catch { /* client parti */ }
            }
        }
        return 0;
    }

    private static void Handle(HttpListenerContext ctx, LocFile doc, string path, string html)
    {
        var req = ctx.Request;
        var route = req.Url?.AbsolutePath ?? "/";

        if (req.HttpMethod == "GET" && route == "/")
        {
            WriteText(ctx.Response, 200, html, "text/html; charset=utf-8");
            return;
        }

        if (req.HttpMethod == "GET" && route == "/api/rows")
        {
            WriteJson(ctx.Response, 200, new { path, rows = doc.Rows.Select(ToDto) });
            return;
        }

        if (req.HttpMethod == "POST" && route == "/api/save")
        {
            using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
            var body = reader.ReadToEnd();
            var rows = JsonSerializer.Deserialize<List<RowDto>>(body, Json) ?? new List<RowDto>();

            // Garde-fou : une virgule dans une valeur casserait le format → on refuse d'écrire (protège le fichier).
            var offenders = rows
                .Where(r => r.Kind == "entry" && (HasComma(r.Key) || HasComma(r.Fr) || HasComma(r.En)))
                .Select(r => r.Key)
                .ToList();
            if (offenders.Count > 0)
            {
                WriteJson(ctx.Response, 400, new { error = "comma", keys = offenders });
                return;
            }

            doc.Rows.Clear();
            doc.Rows.AddRange(rows.Select(FromDto));
            doc.Save(path);
            WriteJson(ctx.Response, 200, new { ok = true });
            return;
        }

        if (req.HttpMethod == "POST" && route == "/api/quit")
        {
            WriteJson(ctx.Response, 200, new { ok = true });
            Environment.Exit(0);
        }

        WriteText(ctx.Response, 404, "not found", "text/plain");
    }

    // ── Sérialisation des lignes ──────────────────────────────────────────────────

    private static object ToDto(LocRow r) => new
    {
        kind = r.Kind.ToString().ToLowerInvariant(),
        key = r.Key,
        fr = r.Fr,
        en = r.En,
        raw = r.Raw,
    };

    private static LocRow FromDto(RowDto d) => new()
    {
        Kind = d.Kind switch
        {
            "header" => RowKind.Header,
            "comment" => RowKind.Comment,
            "blank" => RowKind.Blank,
            _ => RowKind.Entry,
        },
        Key = (d.Key ?? "").Trim(),
        Fr = d.Fr ?? "",
        En = d.En ?? "",
        Raw = d.Raw ?? "",
    };

    private static bool HasComma(string? s) => s is not null && s.IndexOf(',') >= 0;

    // ── Serveur / navigateur ──────────────────────────────────────────────────────

    // Un HttpListener NEUF par tentative : après un Start() en échec (port occupé), l'objet est DISPOSÉ et ne
    // peut plus être réutilisé (ObjectDisposedException). Renvoie celui qui a démarré (laissé actif), ou null.
    private static HttpListener? BindFreePort(out int port)
    {
        for (var p = 5533; p <= 5543; p++)
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{p}/");
            try
            {
                listener.Start();
                port = p;
                return listener;
            }
            catch (HttpListenerException)
            {
                listener.Close();   // port occupé : on libère proprement et on tente le suivant
            }
        }
        port = 0;
        return null;
    }

    private static void OpenBrowser(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { Console.WriteLine("Ouvre l'URL manuellement dans ton navigateur."); }
    }

    private static string LoadHtml()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames().First(n => n.EndsWith("index.html", StringComparison.Ordinal));
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static void WriteText(HttpListenerResponse res, int status, string body, string contentType)
    {
        res.StatusCode = status;
        res.ContentType = contentType;
        var bytes = Encoding.UTF8.GetBytes(body);
        res.ContentLength64 = bytes.Length;
        res.OutputStream.Write(bytes);
        res.Close();
    }

    private static void WriteJson(HttpListenerResponse res, int status, object payload) =>
        WriteText(res, status, JsonSerializer.Serialize(payload, Json), "application/json; charset=utf-8");
}

/// <summary>Forme JSON d'une ligne échangée avec la page (camelCase).</summary>
internal sealed class RowDto
{
    public string Kind { get; set; } = "entry";
    public string? Key { get; set; }
    public string? Fr { get; set; }
    public string? En { get; set; }
    public string? Raw { get; set; }
}
