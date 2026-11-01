using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SaveCloud.Core.Remote
{
    /// <summary>The server answered, but this Tailscale login isn't on its allowlist.</summary>
    public class NotAuthorizedException : Exception
    {
        public string Login { get; }
        public NotAuthorizedException(string login)
            : base(string.IsNullOrEmpty(login)
                ? "The SaveCloud server couldn't identify this PC. Is Tailscale signed in?"
                : $"{login} isn't allowed on this SaveCloud server yet. Ask the server owner to add it.")
        {
            Login = login;
        }
    }

    public class ServerInfo
    {
        public string Service { get; set; }
        public int Api { get; set; }
        public string Login { get; set; }
        public bool Authorized { get; set; }
        public string Mode { get; set; }
    }

    /// <summary>
    /// Talks to server/savecloud_server.py over Tailscale. Tailscale encrypts the traffic and the server identifies
    /// the caller by its Tailscale login, so there are no credentials here at all.
    /// </summary>
    public class HttpStore : IRemoteStore
    {
        private static readonly HttpClient Http = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            Timeout = Timeout.InfiniteTimeSpan, // per-request timeouts below; uploads can be large
        };

        private readonly string _base;

        public HttpStore(string baseUrl) { _base = baseUrl.TrimEnd('/'); }

        public ServerInfo Info { get; private set; }

        /// <summary>Asks a server who we are. Returns null if nothing SaveCloud-like answers.</summary>
        public static async Task<ServerInfo> ProbeAsync(string baseUrl, TimeSpan timeout)
        {
            try
            {
                using (var cts = new CancellationTokenSource(timeout))
                using (var resp = await Http.GetAsync(baseUrl.TrimEnd('/') + "/health", cts.Token).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode) return null;
                    var info = JsonConvert.DeserializeObject<ServerInfo>(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
                    return info?.Service == "savecloud" ? info : null;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is JsonException)
            {
                return null;
            }
        }

        public void Connect()
        {
            Info = ProbeAsync(_base, TimeSpan.FromSeconds(10)).GetAwaiter().GetResult()
                   ?? throw new IOException($"The SaveCloud server at {_base} isn't answering.");
            if (Info.Api != 1) throw new IOException($"The server speaks API version {Info.Api}; update SaveCloud.");
            if (!Info.Authorized) throw new NotAuthorizedException(Info.Login);
        }

        public List<RemoteHead> ReadAllHeads()
            => GetJson<List<RemoteHead>>("/api/v1/games") ?? new List<RemoteHead>();

        public RemoteHead ReadHead(string game)
            => GetJson<RemoteHead>($"/api/v1/games/{Uri.EscapeDataString(Slugs.Of(game))}/head");

        public RemoteHead Publish(string game, long expectedRevision, string archivePath, string archiveSha256,
                                  string fingerprint, List<RemoteFileEntry> files, int historyToKeep, List<string> rules = null)
        {
            var meta = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new
            {
                Game = game,
                Fingerprint = fingerprint,
                ArchiveSha256 = archiveSha256,
                Files = files,
                Rules = rules,
            }));
            string url = $"{_base}/api/v1/games/{Uri.EscapeDataString(Slugs.Of(game))}/revisions?expected={expectedRevision}&keep={historyToKeep}";

            using (var req = new HttpRequestMessage(HttpMethod.Put, url) { Content = new UploadContent(meta, archivePath) })
            {
                req.Headers.Add("X-SaveCloud-Machine", Environment.MachineName);
                using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30)))
                using (var resp = Http.SendAsync(req, cts.Token).GetAwaiter().GetResult())
                {
                    string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    if (resp.StatusCode == (HttpStatusCode)409)
                        throw new RevisionConflictException(JsonConvert.DeserializeObject<RemoteHead>(body));
                    Check(resp, body);
                    return JsonConvert.DeserializeObject<RemoteHead>(body);
                }
            }
        }

        public void DownloadArchive(RemoteHead head, string localPath)
        {
            string url = $"{_base}/api/v1/games/{Uri.EscapeDataString(Slugs.Of(head.Game))}/archive?rev={head.Revision}";
            using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30)))
            using (var resp = Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).GetAwaiter().GetResult())
            {
                if (!resp.IsSuccessStatusCode) Check(resp, resp.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                using (var fs = File.Create(localPath))
                    resp.Content.CopyToAsync(fs).GetAwaiter().GetResult();
            }
            string sha = Hashing.FileSha256(localPath);
            if (!string.Equals(sha, head.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Download of {head.Game} revision {head.Revision} is corrupt (checksum mismatch)");
        }

        private T GetJson<T>(string path) where T : class
        {
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
            using (var resp = Http.GetAsync(_base + path, cts.Token).GetAwaiter().GetResult())
            {
                string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                if (resp.StatusCode == HttpStatusCode.NotFound) return null;
                Check(resp, body);
                return JsonConvert.DeserializeObject<T>(body);
            }
        }

        private void Check(HttpResponseMessage resp, string body)
        {
            if (resp.IsSuccessStatusCode) return;
            if (resp.StatusCode == HttpStatusCode.Forbidden) throw new NotAuthorizedException(Info?.Login);
            string message = null;
            try { message = JsonConvert.DeserializeAnonymousType(body, new { error = "" })?.error; } catch (JsonException) { }
            throw new IOException($"Server error {(int)resp.StatusCode}: {message ?? resp.ReasonPhrase}");
        }

        public void Dispose() { }

        /// <summary>Upload body: 4-byte big-endian metadata length, the metadata JSON, then the zip, streamed.</summary>
        private sealed class UploadContent : HttpContent
        {
            private readonly byte[] _meta;
            private readonly string _file;

            public UploadContent(byte[] meta, string file)
            {
                _meta = meta;
                _file = file;
                Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-savecloud-upload");
            }

            protected override async Task SerializeToStreamAsync(Stream stream, TransportContext context)
            {
                int n = _meta.Length;
                var len = new[] { (byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n };
                await stream.WriteAsync(len, 0, 4).ConfigureAwait(false);
                await stream.WriteAsync(_meta, 0, _meta.Length).ConfigureAwait(false);
                using (var fs = File.OpenRead(_file))
                    await fs.CopyToAsync(stream, 1 << 16).ConfigureAwait(false);
            }

            protected override bool TryComputeLength(out long length)
            {
                length = 4 + _meta.Length + new FileInfo(_file).Length;
                return true;
            }
        }
    }
}
