using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;

namespace IslandEye
{
    public record ToastInfo(string App, string Title, string Body, string AppId = "", string Emoji = "🔔", string Kind = "");

    /// <summary>
    /// Watches Windows' notification history (%LocalAppData%\Microsoft\Windows\Notifications\wpndatabase.db)
    /// and raises an event for every new toast (system + third-party apps).
    /// Read-only and 100% local. Keeps ONE read-only connection open and runs a tiny query every 1.5 s.
    /// </summary>
    public sealed class NotificationWatcher : IDisposable
    {
        public event Action<ToastInfo>? ToastReceived;   // raised on a background thread
        public event Action<string>? StatusChanged;      // "reader active" / error text

        private static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Windows", "Notifications");
        private static readonly string DbPath = Path.Combine(Dir, "wpndatabase.db");

        private const string Sql =
            "SELECT n.\"Order\", n.Id, n.ArrivalTime, n.Payload, h.PrimaryId " +
            "FROM Notification n LEFT JOIN NotificationHandler h ON n.HandlerId = h.RecordId " +
            "WHERE n.Type = 'toast' COLLATE NOCASE AND n.ArrivalTime >= $since " +
            "ORDER BY n.ArrivalTime";

        private readonly Dictionary<string, long> _seen = new();
        private readonly long _startFt = DateTime.UtcNow.AddSeconds(-1).ToFileTimeUtc();
        private readonly CancellationTokenSource _cts = new();
        private bool _useSnapshot, _announcedReady;
        private string _lastError = "";

        private SqliteConnection? _con;
        private SqliteCommand? _cmd;
        private SqliteParameter? _since;

        public void Start() => _ = Task.Run(LoopAsync);

        public void Dispose()
        {
            _cts.Cancel();
            CloseLive();
        }

        private async Task LoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    Poll();
                    if (!_announcedReady)
                    {
                        _announcedReady = true;
                        StatusChanged?.Invoke("Notification reader active" + (_useSnapshot ? " (snapshot mode)" : ""));
                    }
                }
                catch (Exception ex) { ReportError(ex.Message); }

                try { await Task.Delay(_useSnapshot ? 3000 : 1500, _cts.Token); } catch { break; }
            }
        }

        private void ReportError(string msg)
        {
            if (msg == _lastError) return;
            _lastError = msg;
            StatusChanged?.Invoke("Notification reader problem: " + msg);
        }

        private void CloseLive()
        {
            try { _cmd?.Dispose(); } catch { }
            try { _con?.Dispose(); } catch { }
            _cmd = null; _con = null; _since = null;
        }

        private void Poll()
        {
            if (!File.Exists(DbPath))
                throw new FileNotFoundException("Windows notification database not found.");

            if (!_useSnapshot)
            {
                try { QueryLive(); return; }
                catch { CloseLive(); _useSnapshot = true; }   // locked? fall back to reading a copy
            }
            QuerySnapshot();
        }

        private void QueryLive()
        {
            if (_con == null)
            {
                var cs = new SqliteConnectionStringBuilder
                { DataSource = DbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
                _con = new SqliteConnection(cs);
                _con.Open();
                _cmd = _con.CreateCommand();
                _cmd.CommandText = Sql;
                _since = _cmd.Parameters.Add("$since", SqliteType.Integer);
            }
            ReadRows(_cmd!, _since!);
        }

        private void QuerySnapshot()
        {
            string file = Snapshot();
            var cs = new SqliteConnectionStringBuilder
            { DataSource = file, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString();
            using var con = new SqliteConnection(cs);
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = Sql;
            var p = cmd.Parameters.Add("$since", SqliteType.Integer);
            ReadRows(cmd, p);
        }

        private static string Snapshot()
        {
            string dir = Path.Combine(Path.GetTempPath(), "IslandEye_wpn");
            Directory.CreateDirectory(dir);
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                string src = DbPath + suffix, dst = Path.Combine(dir, "wpndatabase.db" + suffix);
                try
                {
                    if (File.Exists(src))
                    {
                        using var i = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using var o = new FileStream(dst, FileMode.Create, FileAccess.Write);
                        i.CopyTo(o);
                    }
                    else if (File.Exists(dst)) File.Delete(dst);
                }
                catch { }
            }
            return Path.Combine(dir, "wpndatabase.db");
        }

        private void ReadRows(SqliteCommand cmd, SqliteParameter sinceParam)
        {
            long since = Math.Max(_startFt, DateTime.UtcNow.AddMinutes(-10).ToFileTimeUtc());
            sinceParam.Value = since;

            List<ToastInfo>? fresh = null;
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    long order = r.GetInt64(0);
                    long id = r.IsDBNull(1) ? 0 : r.GetInt64(1);
                    long ft = r.GetInt64(2);
                    string key = $"{order}:{id}:{ft}";
                    if (_seen.ContainsKey(key)) continue;
                    _seen[key] = ft;

                    string xml = "";
                    if (!r.IsDBNull(3))
                    {
                        object v = r.GetValue(3);
                        xml = v is byte[] b ? Decode(b) : v.ToString() ?? "";
                    }
                    string appRaw = r.IsDBNull(4) ? "" : r.GetString(4);
                    var t = Parse(FriendlyName(appRaw), appRaw, xml);
                    if (t != null) (fresh ??= new List<ToastInfo>()).Add(t);
                }
            }

            if (_seen.Count > 0)
                foreach (var k in _seen.Where(p => p.Value < since).Select(p => p.Key).ToList()) _seen.Remove(k);
            if (fresh != null) foreach (var t in fresh) ToastReceived?.Invoke(t);
        }

        private static string Decode(byte[] b)
        {
            if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b);
            return Encoding.UTF8.GetString(b);
        }

        private static ToastInfo? Parse(string app, string appId, string xml)
        {
            try
            {
                int i = xml.IndexOf('<');
                if (i < 0) return null;
                var doc = XDocument.Parse(xml[i..].Trim('\0', ' ', '\r', '\n'));
                var texts = doc.Descendants("text")
                    .Where(t => (string?)t.Attribute("placement") != "attribution")
                    .Select(t => t.Value.Trim())
                    .Where(s => s.Length > 0)
                    .ToList();
                if (texts.Count == 0) return null;
                return new ToastInfo(app, texts[0], string.Join(" ", texts.Skip(1)), appId);
            }
            catch { return null; }
        }

        /// <summary>Turns an app id like "5319275A.WhatsAppDesktop_cv1g1gvanyjgm!App" into "WhatsAppDesktop".</summary>
        internal static string FriendlyName(string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return "Notification";
            string s = id;
            int bang = s.IndexOf('!');
            if (bang > 0)
            {
                s = s[..bang];
                int us = s.IndexOf('_');
                if (us > 0) s = s[..us];
            }
            s = s.TrimEnd('\\', '/');
            int sep = s.LastIndexOfAny(new[] { '\\', '/' });
            if (sep >= 0) s = s[(sep + 1)..];
            if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
            int dot = s.LastIndexOf('.');
            if (dot > 0 && dot < s.Length - 1) s = s[(dot + 1)..];
            return s.Length == 0 ? "Notification" : s;
        }
    }
}
