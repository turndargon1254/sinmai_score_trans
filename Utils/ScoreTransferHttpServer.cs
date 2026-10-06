using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using MelonLoader;
using SinmaiAssist.Cheat;
using SinmaiAssist.Types;

namespace SinmaiAssist.Utils
{
    /// <summary>
    /// 分数转移输入服务（基于 TcpListener，无需管理员权限）。
    /// 提供歌曲列表、单曲转移、批量转移与状态查询。
    /// 用户只需提供：目标歌曲、目标难度、目标完成度，或一份待转移成绩列表。
    /// </summary>
    public static class ScoreTransferHttpServer
    {
        private static TcpListener _listener;
        private static Thread _acceptThread;
        private static volatile bool _running;

        public static void Init(int port)
        {
            Stop();
            try
            {
                _listener = new TcpListener(IPAddress.Any, port);
                _listener.Start();
                _running = true;
                _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "ScoreTransferHttp" };
                _acceptThread.Start();
                MelonLogger.Msg($"[ScoreTransfer] 网页服务已启动: http://<本机IP>:{port}/");
            }
            catch (Exception e)
            {
                MelonLogger.Error($"[ScoreTransfer] 网页服务启动失败: {e}");
            }
        }

        public static void Stop()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
            _listener = null;
        }

        private static void AcceptLoop()
        {
            while (_running)
            {
                try
                {
                    TcpClient client = _listener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(state => HandleClient((TcpClient)state), client);
                }
                catch (Exception e)
                {
                    if (_running)
                    {
                        MelonLogger.Warning($"[ScoreTransfer] 接受连接失败: {e.Message}");
                    }
                }
            }
        }

        private static void HandleClient(TcpClient client)
        {
            try
            {
                using (client)
                using (NetworkStream stream = client.GetStream())
                {
                    client.ReceiveTimeout = 10000;
                    client.SendTimeout = 10000;

                    string method;
                    string path;
                    string query;
                    string body;
                    if (!ReadRequest(stream, out method, out path, out query, out body))
                    {
                        Write(stream, "400 Bad Request", "text/plain; charset=utf-8", "bad request");
                        return;
                    }

                    if (method == "GET" && (path == "/" || path == "/index.html"))
                    {
                        Write(stream, "200 OK", "text/html; charset=utf-8", IndexHtml);
                    }
                    else if (method == "GET" && path == "/api/allMusic")
                    {
                        Write(stream, "200 OK", "application/json; charset=utf-8", BuildAllMusicJson());
                    }
                    else if (method == "GET" && path == "/api/status")
                    {
                        Write(stream, "200 OK", "application/json; charset=utf-8", BuildStatusJson());
                    }
                    else if (method == "GET" && path == "/api/search")
                    {
                        Write(stream, "200 OK", "application/json; charset=utf-8", HandleSearch(query));
                    }
                    else if (method == "POST" && path == "/api/transfer")
                    {
                        Write(stream, "200 OK", "application/json; charset=utf-8", HandleTransfer(body));
                    }
                    else if (method == "POST" && path == "/api/transferBatch")
                    {
                        Write(stream, "200 OK", "application/json; charset=utf-8", HandleTransferBatch(body));
                    }
                    else if (method == "GET" && path == "/api/batchStatus")
                    {
                        Write(stream, "200 OK", "application/json; charset=utf-8", BuildBatchStatusJson());
                    }
                    else if (method == "POST" && path == "/api/batchStop")
                    {
                        BatchTransfer.RequestStop();
                        Write(stream, "200 OK", "application/json; charset=utf-8", "{\"ok\":true}");
                    }
                    else if (method == "POST" && path == "/api/login")
                    {
                        Write(stream, "200 OK", "application/json; charset=utf-8", HandleLogin(body));
                    }
                    else
                    {
                        Write(stream, "404 Not Found", "text/plain; charset=utf-8", "not found");
                    }
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[ScoreTransfer] 处理请求失败: {e.Message}");
            }
        }

        private static string HandleTransfer(string body)
        {
            int musicId = GetJsonInt(body, "musicId", 0);
            int scoreType = GetJsonInt(body, "scoreType", musicId >= 10000 ? 1 : 0);
            int difficulty = GetJsonInt(body, "difficulty", 3);
            decimal achievement = GetJsonDecimal(body, "achievement", 100m);

            string error;
            if (!ScoreTransfer.Request(musicId, scoreType, difficulty, achievement, out error))
            {
                return "{\"ok\":false,\"error\":\"" + JsonEscape(error) + "\"}";
            }
            return "{\"ok\":true,\"state\":\"" + JsonEscape(ScoreTransfer.State) + "\"}";
        }

        private static string HandleTransferBatch(string body)
        {
            int batchSize;
            string error;
            List<TransferItem> items = ParseItems(body, out batchSize, out error);
            if (items == null)
            {
                return "{\"ok\":false,\"error\":\"" + JsonEscape(error) + "\"}";
            }
            if (batchSize > 0)
            {
                SinmaiAssist.config.ScoreTransfer.BatchSize = batchSize;
            }
            if (!BatchTransfer.RequestStart(items, out error))
            {
                return "{\"ok\":false,\"error\":\"" + JsonEscape(error) + "\"}";
            }
            return "{\"ok\":true,\"count\":" + items.Count + "}";
        }

        private static string HandleLogin(string body)
        {
            if (SinmaiAssist.config.DummyLogin == null || !SinmaiAssist.config.DummyLogin.Enable)
            {
                return "{\"ok\":false,\"error\":\"自动登录未启用 (dummyLogin.enable)\"}";
            }

            string mode = GetJsonString(body, "mode");
            string code = GetJsonString(body, "code");
            string userId = GetJsonString(body, "userId");

            bool codeMode = !string.IsNullOrEmpty(code) || ModeEquals(mode, "code");
            bool userMode = !string.IsNullOrEmpty(userId) || ModeEquals(mode, "userId") || ModeEquals(mode, "userid");

            if (codeMode)
            {
                DummyLoginState.RequestCodeLogin(code);
                return "{\"ok\":true}";
            }
            if (userMode)
            {
                DummyLoginState.RequestUserIdLogin(userId);
                return "{\"ok\":true}";
            }
            return "{\"ok\":false,\"error\":\"请提供 code 或 userId\"}";
        }

        private static bool ModeEquals(string value, string expected)
        {
            return !string.IsNullOrEmpty(value) && value.Equals(expected, StringComparison.OrdinalIgnoreCase);
        }

        private static int ParseDifficulty(string s)
        {
            if (int.TryParse(s, out int n))
            {
                return n;
            }
            switch (s.Trim().ToUpperInvariant())
            {
                case "BASIC": case "BSC": return 0;
                case "ADVANCED": case "ADV": return 1;
                case "EXPERT": case "EXP": return 2;
                case "MASTER": case "MAS": return 3;
                case "RE:MASTER": case "REMASTER": case "REMAS": return 4;
                default: return 3;
            }
        }

        private static List<TransferItem> ParseItems(string body, out int batchSize, out string error)
        {
            batchSize = 0;
            error = null;
            List<TransferItem> items = new List<TransferItem>();
            if (string.IsNullOrWhiteSpace(body))
            {
                error = "请求内容为空";
                return null;
            }

            body = body.Trim();
            if (body.StartsWith("{"))
            {
                batchSize = GetJsonInt(body, "batchSize", 0);

                int itemsIndex = body.IndexOf("\"items\"", StringComparison.OrdinalIgnoreCase);
                string arrayText = itemsIndex >= 0 ? body.Substring(itemsIndex) : body;
                int open = arrayText.IndexOf('[');
                int close = arrayText.LastIndexOf(']');
                if (open < 0 || close <= open)
                {
                    error = "未找到 items 数组";
                    return null;
                }

                string inner = arrayText.Substring(open + 1, close - open - 1);
                int cursor = 0;
                while (true)
                {
                    int start = inner.IndexOf('{', cursor);
                    if (start < 0) break;
                    int end = inner.IndexOf('}', start);
                    if (end < 0) break;
                    string obj = inner.Substring(start, end - start + 1);
                    cursor = end + 1;

                    TransferItem item = new TransferItem
                    {
                        musicId = GetJsonInt(obj, "musicId", 0),
                        scoreType = GetJsonInt(obj, "scoreType", -1),
                        difficulty = GetJsonInt(obj, "difficulty", 3),
                        targetAchievement = GetJsonDecimal(obj, "achievement", 100m),
                        name = GetJsonString(obj, "name")
                    };
                    if (item.scoreType < 0)
                    {
                        item.scoreType = item.musicId >= 10000 ? 1 : 0;
                    }
                    if (item.musicId > 0)
                    {
                        items.Add(item);
                    }
                }
            }
            else
            {
                // 纯文本：每行 musicId,difficulty,achievement[,scoreType][,name]
                string[] lines = body.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string rawLine in lines)
                {
                    string line = rawLine.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//"))
                    {
                        continue;
                    }
                    if (line.StartsWith("batchSize=", StringComparison.OrdinalIgnoreCase))
                    {
                        int.TryParse(line.Substring("batchSize=".Length).Trim(), out batchSize);
                        continue;
                    }
                    string[] parts = line.Split(new[] { ',', ' ', '\t', ';' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 3)
                    {
                        continue;
                    }
                    int musicId;
                    decimal achievement;
                    if (!int.TryParse(parts[0], out musicId) ||
                        !decimal.TryParse(parts[2], NumberStyles.Number, CultureInfo.InvariantCulture, out achievement))
                    {
                        continue;
                    }
                    int difficulty = ParseDifficulty(parts[1]);
                    int scoreType = musicId >= 10000 ? 1 : 0;
                    // 完成度之后的可选字段：状态(ap+/ap/fc+/fc)、dx(整数)、sd/dx(谱面)、名称
                    int comboStatus = -1;
                    int dx = -1;
                    string name = "";
                    for (int j = 3; j < parts.Length; j++)
                    {
                        string c = parts[j].Trim();
                        string lc = c.ToLowerInvariant();
                        if (lc == "sd")
                        {
                            scoreType = 0;
                        }
                        else if (lc == "dx")
                        {
                            scoreType = 1;
                        }
                        else if (lc == "ap+")
                        {
                            comboStatus = 0;
                        }
                        else if (lc == "ap")
                        {
                            comboStatus = 1;
                        }
                        else if (lc == "fc+")
                        {
                            comboStatus = 2;
                        }
                        else if (lc == "fc")
                        {
                            comboStatus = 3;
                        }
                        else if (int.TryParse(c, out int dv))
                        {
                            dx = dv;
                        }
                        else
                        {
                            name = c;
                        }
                    }
                    TransferItem item = new TransferItem
                    {
                        musicId = musicId,
                        difficulty = difficulty,
                        targetAchievement = achievement,
                        scoreType = scoreType,
                        comboStatus = comboStatus,
                        dx = dx,
                        name = name
                    };
                    items.Add(item);
                }
            }

            if (items.Count == 0)
            {
                error = "没有解析到任何有效曲目";
                return null;
            }
            return items;
        }

        private static string BuildBatchStatusJson()
        {
            List<TransferItem> items = BatchTransfer.Items;
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"running\":").Append(BatchTransfer.Running ? "true" : "false");
            sb.Append(",\"state\":\"").Append(JsonEscape(BatchTransfer.State)).Append('"');
            sb.Append(",\"message\":\"").Append(JsonEscape(BatchTransfer.Message)).Append('"');
            sb.Append(",\"currentBatch\":").Append(BatchTransfer.CurrentBatch);
            sb.Append(",\"totalBatches\":").Append(BatchTransfer.TotalBatches);
            sb.Append(",\"completed\":").Append(BatchTransfer.CompletedCount);
            sb.Append(",\"failed\":").Append(BatchTransfer.FailedCount);
            sb.Append(",\"items\":[");
            for (int i = 0; i < items.Count; i++)
            {
                TransferItem item = items[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"musicId\":").Append(item.musicId);
                sb.Append(",\"name\":\"").Append(JsonEscape(item.name)).Append('"');
                sb.Append(",\"difficulty\":").Append(item.difficulty);
                sb.Append(",\"originalAchievement\":").Append(item.originalAchievement);
                sb.Append(",\"targetAchievement\":").Append(item.targetAchievement.ToString(CultureInfo.InvariantCulture));
                sb.Append(",\"batchIndex\":").Append(item.batchIndex);
                sb.Append(",\"status\":\"").Append(JsonEscape(item.status)).Append('"');
                sb.Append(",\"error\":\"").Append(JsonEscape(item.error)).Append("\"}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static string BuildStatusJson()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"state\":\"").Append(JsonEscape(ScoreTransfer.State)).Append('"');
            sb.Append(",\"message\":\"").Append(JsonEscape(ScoreTransfer.Message)).Append('"');
            sb.Append(",\"musicId\":").Append(ScoreTransfer.CurrentMusicId);
            sb.Append(",\"difficulty\":").Append(ScoreTransfer.CurrentDifficulty);
            sb.Append(",\"achievement\":").Append(ScoreTransfer.CurrentAchievement.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"inSongSelect\":").Append(MusicSelect.IsReady ? "true" : "false");
            sb.Append(",\"loginMessage\":\"").Append(JsonEscape(DummyLoginState.LastMessage)).Append('"');
            sb.Append('}');
            return sb.ToString();
        }

        private static string BuildAllMusicJson()
        {
            return BuildSongsJson(SongDatabase.Songs);
        }

        private static string HandleSearch(string query)
        {
            string q = GetQueryParam(query, "q");
            int limit = 0;
            int.TryParse(GetQueryParam(query, "limit"), out limit);
            return BuildSongsJson(SongDatabase.Search(q, limit));
        }

        private static string GetQueryParam(string query, string key)
        {
            if (string.IsNullOrEmpty(query))
            {
                return "";
            }
            foreach (string pair in query.Split('&'))
            {
                int eq = pair.IndexOf('=');
                string k = eq >= 0 ? pair.Substring(0, eq) : pair;
                if (k.Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    string v = eq >= 0 ? pair.Substring(eq + 1) : "";
                    try
                    {
                        return Uri.UnescapeDataString(v.Replace("+", " "));
                    }
                    catch
                    {
                        return v;
                    }
                }
            }
            return "";
        }

        private static string BuildSongsJson(List<TransferSongInfo> list)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append('[');
            bool first = true;
            foreach (TransferSongInfo song in list)
            {
                if (!first)
                {
                    sb.Append(',');
                }
                first = false;

                sb.Append("{\"id\":").Append(song.id);
                sb.Append(",\"scoreType\":").Append(song.scoreType);
                sb.Append(",\"name\":\"").Append(JsonEscape(song.name)).Append('"');
                sb.Append(",\"artist\":\"").Append(JsonEscape(song.artist)).Append('"');
                sb.Append(",\"charts\":[");
                for (int d = 0; d < song.charts.Length; d++)
                {
                    if (d > 0)
                    {
                        sb.Append(',');
                    }
                    TransferChartInfo chart = song.charts[d];
                    sb.Append("{\"enable\":").Append(chart.enable ? "true" : "false");
                    sb.Append(",\"level\":").Append(chart.level);
                    sb.Append(",\"levelDecimal\":").Append(chart.levelDecimal);
                    sb.Append(",\"designer\":\"").Append(JsonEscape(chart.designer)).Append("\"}");
                }
                sb.Append("]}");
            }
            sb.Append(']');
            return sb.ToString();
        }

        private static bool ReadRequest(NetworkStream stream, out string method, out string path, out string query, out string body)
        {
            method = "GET";
            path = "/";
            query = "";
            body = "";

            byte[] buffer = new byte[8192];
            MemoryStream received = new MemoryStream();
            int headerEnd = -1;

            while (headerEnd < 0)
            {
                int read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    break;
                }
                received.Write(buffer, 0, read);
                headerEnd = IndexOf(received.GetBuffer(), (int)received.Length, "\r\n\r\n");
            }

            if (headerEnd < 0)
            {
                return false;
            }

            string headerText = Encoding.UTF8.GetString(received.GetBuffer(), 0, headerEnd);
            string[] lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] firstLine = lines[0].Split(' ');
            if (firstLine.Length >= 1)
            {
                method = firstLine[0];
            }
            if (firstLine.Length >= 2)
            {
                path = firstLine[1];
            }

            int queryIndex = path.IndexOf('?');
            if (queryIndex >= 0)
            {
                query = path.Substring(queryIndex + 1);
                path = path.Substring(0, queryIndex);
            }

            int contentLength = 0;
            foreach (string line in lines)
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(line.Substring("Content-Length:".Length).Trim(), out contentLength);
                }
            }

            int bodyStart = headerEnd + 4;
            MemoryStream bodyStream = new MemoryStream();
            int alreadyRead = (int)received.Length - bodyStart;
            if (alreadyRead > 0)
            {
                bodyStream.Write(received.GetBuffer(), bodyStart, alreadyRead);
            }
            while (bodyStream.Length < contentLength)
            {
                int read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    break;
                }
                bodyStream.Write(buffer, 0, read);
            }

            body = Encoding.UTF8.GetString(bodyStream.GetBuffer(), 0, (int)bodyStream.Length);
            return true;
        }

        private static int IndexOf(byte[] buffer, int length, string needle)
        {
            byte[] pattern = Encoding.ASCII.GetBytes(needle);
            int limit = length - pattern.Length;
            for (int i = 0; i <= limit; i++)
            {
                bool match = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (buffer[i + j] != pattern[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                {
                    return i;
                }
            }
            return -1;
        }

        private static void Write(NetworkStream stream, string status, string contentType, string body)
        {
            byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
            string header = "HTTP/1.1 " + status + "\r\n" +
                            "Content-Type: " + contentType + "\r\n" +
                            "Content-Length: " + bodyBytes.Length + "\r\n" +
                            "Cache-Control: no-store\r\n" +
                            "Connection: close\r\n\r\n";
            byte[] headerBytes = Encoding.ASCII.GetBytes(header);
            stream.Write(headerBytes, 0, headerBytes.Length);
            stream.Write(bodyBytes, 0, bodyBytes.Length);
            stream.Flush();
        }

        private static int GetJsonInt(string json, string key, int fallback)
        {
            if (string.IsNullOrEmpty(json))
            {
                return fallback;
            }
            int index = json.IndexOf("\"" + key + "\"", StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return fallback;
            }
            index = json.IndexOf(':', index);
            if (index < 0)
            {
                return fallback;
            }
            index++;
            while (index < json.Length && (json[index] == ' ' || json[index] == '\t' || json[index] == '"'))
            {
                index++;
            }
            int start = index;
            while (index < json.Length && (char.IsDigit(json[index]) || json[index] == '-'))
            {
                index++;
            }
            if (index <= start)
            {
                return fallback;
            }
            int value;
            return int.TryParse(json.Substring(start, index - start), out value) ? value : fallback;
        }

        private static string GetJsonString(string json, string key)
        {
            if (string.IsNullOrEmpty(json))
            {
                return "";
            }
            int index = json.IndexOf("\"" + key + "\"", StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return "";
            }
            index = json.IndexOf(':', index);
            if (index < 0)
            {
                return "";
            }
            index++;
            while (index < json.Length && char.IsWhiteSpace(json[index]))
            {
                index++;
            }
            if (index >= json.Length || json[index] != '"')
            {
                return "";
            }
            index++;
            int start = index;
            while (index < json.Length && json[index] != '"')
            {
                index++;
            }
            return index <= json.Length ? json.Substring(start, index - start) : "";
        }

        private static decimal GetJsonDecimal(string json, string key, decimal fallback)
        {
            if (string.IsNullOrEmpty(json))
            {
                return fallback;
            }
            int index = json.IndexOf("\"" + key + "\"", StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return fallback;
            }
            index = json.IndexOf(':', index);
            if (index < 0)
            {
                return fallback;
            }
            index++;
            while (index < json.Length && (char.IsWhiteSpace(json[index]) || json[index] == '"'))
            {
                index++;
            }
            int start = index;
            while (index < json.Length && (char.IsDigit(json[index]) || json[index] == '.' || json[index] == '-' || json[index] == '+'))
            {
                index++;
            }
            if (index <= start)
            {
                return fallback;
            }
            decimal value;
            return decimal.TryParse(json.Substring(start, index - start), NumberStyles.Number, CultureInfo.InvariantCulture, out value)
                ? value
                : fallback;
        }

        private static string JsonEscape(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "";
            }
            StringBuilder sb = new StringBuilder(value.Length + 8);
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            return sb.ToString();
        }

        private const string IndexHtml = @"<!DOCTYPE html>
<html lang=""zh-CN"">
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1"">
<title>Sinmai 分数转移</title>
<style>
  * { box-sizing: border-box; }
  body { margin: 0; font-family: system-ui, -apple-system, ""Segoe UI"", sans-serif; background: #12151c; color: #e6e9ef; }
  header { padding: 14px 18px; background: #1b1f2a; position: sticky; top: 0; z-index: 5; border-bottom: 1px solid #2a3040; }
  h1 { font-size: 16px; margin: 0 0 10px; }
  input[type=text], input[type=number], select, textarea { width: 100%; padding: 9px 10px; border-radius: 8px; border: 1px solid #333b4d; background: #0e1117; color: #e6e9ef; font-size: 14px; }
  .row { display: flex; gap: 10px; flex-wrap: wrap; }
  .col { flex: 1 1 150px; }
  label { display: block; font-size: 12px; color: #9aa4b8; margin: 8px 0 4px; }
  #list { padding: 8px 12px 140px; }
  .song { padding: 10px 12px; border-radius: 8px; border: 1px solid #232a38; margin-bottom: 8px; cursor: pointer; background: #171b24; }
  .song:hover { border-color: #4b6bff; }
  .song.sel { border-color: #4b6bff; background: #1d2436; }
  .song .n { font-size: 14px; }
  .song .m { font-size: 12px; color: #8b95a8; margin-top: 3px; }
  .bar { position: fixed; left: 0; right: 0; bottom: 0; background: #1b1f2a; border-top: 1px solid #2a3040; padding: 12px 16px; }
  button { width: 100%; padding: 12px; border: none; border-radius: 8px; background: #4b6bff; color: #fff; font-size: 15px; cursor: pointer; }
  button:disabled { background: #333b4d; color: #8b95a8; cursor: not-allowed; }
  #status { font-size: 12px; color: #9aa4b8; margin-top: 8px; min-height: 16px; text-align: center; }
  .tag { display: inline-block; font-size: 11px; padding: 1px 6px; border-radius: 6px; background: #2a3040; margin-left: 6px; color: #aab4c8; }
</style>
</head>
<body>
<header>
  <h1>Sinmai 分数转移 <span class=""tag"">Auto</span></h1>
  <input type=""text"" id=""search"" placeholder=""搜索曲名 / 曲师 / 曲目ID / 谱面作者"" autocomplete=""off"">
  <div class=""row"" style=""margin-top:10px"">
    <div class=""col"">
      <label>难度</label>
      <select id=""difficulty"">
        <option value=""0"">Basic</option>
        <option value=""1"">Advanced</option>
        <option value=""2"">Expert</option>
        <option value=""3"" selected>Master</option>
        <option value=""4"">Re:Master</option>
      </select>
    </div>
    <div class=""col"">
      <label>谱面类型</label>
      <select id=""scoreType"">
        <option value=""-1"">跟随所选曲目</option>
        <option value=""0"">标准 (SD)</option>
        <option value=""1"">DX</option>
      </select>
    </div>
    <div class=""col"">
      <label>完成度 (0-101, 支持小数)</label>
      <input type=""number"" id=""achievement"" value=""100.0000"" min=""0"" max=""101"" step=""0.0001"">
    </div>
  </div>
  <details style=""margin-top:10px"">
    <summary style=""cursor:pointer;font-size:13px;color:#9aa4b8"">批量转移 (每批最多 4 首)</summary>
    <div style=""margin-top:8px"">
      <label>每行一首：musicId,难度,完成度 (可选第4列 scoreType 0=SD 1=DX)</label>
      <textarea id=""batchText"" rows=""4"" placeholder=""1234,3,100.5000
5678,4,101.0000,1""></textarea>
      <div class=""row"" style=""margin-top:8px"">
        <div class=""col""><label>每批数量</label><input type=""number"" id=""batchSize"" value=""4"" min=""1"" max=""4""></div>
        <div class=""col"" style=""display:flex;align-items:flex-end;gap:8px"">
          <button id=""batchGo"" style=""width:50%"">开始批量</button>
          <button id=""batchStop"" style=""width:50%;background:#8b3b3b"">停止</button>
        </div>
      </div>
    </div>
  </details>
  <details style=""margin-top:10px"">
    <summary style=""cursor:pointer;font-size:13px;color:#9aa4b8"">登录（二维码 / UserID）</summary>
    <div style=""margin-top:8px"">
      <label>二维码内容 / Aime Code</label>
      <input type=""text"" id=""loginCode"" placeholder=""20 位数字或二维码内容"" autocomplete=""off"">
      <button id=""loginCodeBtn"" style=""margin-top:8px"">二维码登录</button>
      <label style=""margin-top:10px"">UserID</label>
      <input type=""text"" id=""loginUser"" placeholder=""UserID"" autocomplete=""off"">
      <button id=""loginUserBtn"" style=""margin-top:8px"">UserID 登录</button>
    </div>
  </details>
</header>
<div id=""list""></div>
<div class=""bar"">
  <div style=""display:flex;gap:8px"">
    <button id=""go"" disabled>选择一首歌曲</button>
    <button id=""addBatch"" disabled style=""background:#2f6b45"">加入批量</button>
  </div>
  <div id=""status"">正在加载歌曲列表...</div>
</div>
<script>
let songs = [];
let selected = null;
const listEl = document.getElementById('list');
const statusEl = document.getElementById('status');
const goBtn = document.getElementById('go');
const addBatchBtn = document.getElementById('addBatch');
const searchEl = document.getElementById('search');

function esc(s){ return (s||'').replace(/[&<>""]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','""':'&quot;'}[c])); }
function diffName(d){ return ['Basic','Advanced','Expert','Master','Re:Master'][d]; }

function render(){
  const filtered = songs;
  listEl.innerHTML = filtered.slice(0, 300).map(s => {
    const sel = selected && selected.id === s.id ? ' sel' : '';
    const dx = s.scoreType === 1 ? '<span class=""tag"">DX</span>' : '<span class=""tag"">SD</span>';
    const lv = (s.charts||[]).map((c,i)=> c.enable ? (diffName(i)+':'+c.level+(c.levelDecimal?'+':'')) : null).filter(Boolean).slice(0,5).join('  ');
    return '<div class=""song'+sel+'"" data-id=""'+s.id+'"" data-st=""'+s.scoreType+'""><div class=""n"">'+esc(s.name)+dx+'</div><div class=""m"">ID '+s.id+(s.artist?(' · '+esc(s.artist)):'')+'</div><div class=""m"">'+lv+'</div></div>';
  }).join('');
  Array.prototype.forEach.call(listEl.querySelectorAll('.song'), el => {
    el.onclick = () => {
      const id = parseInt(el.getAttribute('data-id'),10);
      const st = parseInt(el.getAttribute('data-st'),10);
      selected = { id: id, scoreType: st };
      const s = songs.find(x => x.id === id);
      goBtn.disabled = false;
      addBatchBtn.disabled = false;
      goBtn.textContent = '转移：' + (s?s.name:id) + ' (' + diffName(parseInt(document.getElementById('difficulty').value,10)) + ')';
      render();
    };
  });
}

function effectiveChoice(){
  const stSel = parseInt(document.getElementById('scoreType').value, 10);
  let id = selected.id;
  let st = stSel >= 0 ? stSel : selected.scoreType;
  const base = id % 10000;
  const wantId = st === 1 ? base + 10000 : base;
  if (songs.some(x => x.id === wantId)) id = wantId;
  return { id: id, scoreType: st };
}

function refreshStatus(){
  fetch('/api/status').then(r=>r.json()).then(st => {
    statusEl.textContent = st.state + (st.message ? (' · ' + st.message) : '') + (st.inSongSelect ? '' : ' · 请进入选歌界面');
  }).catch(()=>{});
}

goBtn.onclick = () => {
  if(!selected) return;
  const choice = effectiveChoice();
  const difficulty = parseInt(document.getElementById('difficulty').value,10);
  const achievement = parseFloat(document.getElementById('achievement').value);
  goBtn.disabled = true;
  statusEl.textContent = '提交中...';
  fetch('/api/transfer', {
    method:'POST',
    headers:{'Content-Type':'application/json'},
    body: JSON.stringify({ musicId: choice.id, scoreType: choice.scoreType, difficulty: difficulty, achievement: achievement })
  }).then(r=>r.json()).then(res => {
    if(!res.ok){ statusEl.textContent = '失败: ' + res.error; goBtn.disabled = false; }
    else { statusEl.textContent = '已提交，等待游戏进入曲目...'; setTimeout(()=>{ goBtn.disabled=false; refreshStatus(); }, 1500); }
  }).catch(e => { statusEl.textContent = '请求失败: ' + e; goBtn.disabled = false; });
};

addBatchBtn.onclick = () => {
  if(!selected) return;
  const choice = effectiveChoice();
  const difficulty = parseInt(document.getElementById('difficulty').value,10);
  const achievement = parseFloat(document.getElementById('achievement').value);
  const ta = document.getElementById('batchText');
  ta.value = (ta.value ? ta.value.replace(/\s*$/, '') + '\n' : '') + choice.id + ',' + difficulty + ',' + achievement + ',' + choice.scoreType;
  statusEl.textContent = '已加入批量列表';
};

function renderBatch(batch){
  const items = batch.items || [];
  listEl.innerHTML = items.map((it, i) => {
    const color = it.status === 'Done' ? '#4bff9e' : (it.status === 'Failed' ? '#ff6b6b' : '#e6e9ef');
    const orig = (it.originalAchievement / 10000).toFixed(4);
    const tgt = Number(it.targetAchievement).toFixed(4);
    return '<div class=""song""><div class=""n"" style=""color:' + color + '"">#' + (i+1) + ' [B' + it.batchIndex + '] ' + esc(it.name || ('' + it.musicId)) + '</div><div class=""m"">难度 ' + diffName(it.difficulty) + ' · ' + orig + '% → ' + tgt + '% · ' + it.status + (it.error ? (' · ' + esc(it.error)) : '') + '</div></div>';
  }).join('');
}

function refreshBatch(){
  fetch('/api/batchStatus').then(r => r.json()).then(b => {
    if(b.items && b.items.length){
      statusEl.textContent = (b.running ? '运行中 ' : '') + b.state + ' · 成功 ' + b.completed + ' / 失败 ' + b.failed;
      if(b.running) renderBatch(b);
    }
  }).catch(()=>{});
}

document.getElementById('batchGo').onclick = () => {
  const text = document.getElementById('batchText').value;
  const batchSize = parseInt(document.getElementById('batchSize').value, 10);
  if(!text.trim()){ statusEl.textContent = '请填写批量列表'; return; }
  statusEl.textContent = '提交批量任务...';
  fetch('/api/transferBatch', {method:'POST', headers:{'Content-Type':'text/plain'}, body: 'batchSize=' + batchSize + '\n' + text})
    .then(r => r.json()).then(res => {
      if(!res.ok){ statusEl.textContent = '批量失败: ' + res.error; }
      else { statusEl.textContent = '批量任务已提交 (' + res.count + ' 首)'; refreshBatch(); }
    }).catch(e => { statusEl.textContent = '请求失败: ' + e; });
};

document.getElementById('batchStop').onclick = () => {
  fetch('/api/batchStop', {method:'POST'}).then(() => { statusEl.textContent = '已请求停止'; });
};

document.getElementById('loginCodeBtn').onclick = () => {
  const code = document.getElementById('loginCode').value.trim();
  if(!code){ statusEl.textContent = '请输入二维码内容'; return; }
  fetch('/api/login', {method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({mode:'code', code: code})})
    .then(r => r.json()).then(res => { statusEl.textContent = res.ok ? '已提交二维码登录' : ('登录失败: ' + res.error); })
    .catch(e => { statusEl.textContent = '请求失败: ' + e; });
};

document.getElementById('loginUserBtn').onclick = () => {
  const userId = document.getElementById('loginUser').value.trim();
  if(!userId){ statusEl.textContent = '请输入 UserID'; return; }
  fetch('/api/login', {method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({mode:'userId', userId: userId})})
    .then(r => r.json()).then(res => { statusEl.textContent = res.ok ? '已提交 UserID 登录' : ('登录失败: ' + res.error); })
    .catch(e => { statusEl.textContent = '请求失败: ' + e; });
};

let searchTimer = null;
function doSearch(){
  const q = searchEl.value.trim();
  fetch('/api/search?q=' + encodeURIComponent(q) + '&limit=300')
    .then(r => r.json()).then(data => {
      songs = data || [];
      render();
      statusEl.textContent = '搜索结果: ' + songs.length + ' 首';
    }).catch(e => { statusEl.textContent = '搜索失败: ' + e; });
}
searchEl.oninput = () => { if(searchTimer) clearTimeout(searchTimer); searchTimer = setTimeout(doSearch, 200); };
doSearch();

setInterval(refreshStatus, 2000);
setInterval(refreshBatch, 1500);
refreshStatus();
</script>
</body>
</html>";
    }
}
