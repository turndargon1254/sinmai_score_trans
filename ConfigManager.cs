using System;
using System.Globalization;
using System.IO;

namespace SinmaiAssist
{
    /// <summary>
    /// 极简直白的配置读取（仅解析本项目所需的扁平 section/key: value）。
    /// 不依赖第三方 YAML 库。
    /// </summary>
    public class ConfigManager
    {
        private Config _config = new Config
        {
            ScoreTransfer = new ScoreTransferConfig(),
            DummyLogin = new DummyLoginConfig()
        };

        public void Initialize(string yamlFilePath)
        {
            Config config = new Config
            {
                ScoreTransfer = new ScoreTransferConfig(),
                DummyLogin = new DummyLoginConfig()
            };

            string section = null;
            foreach (string raw in File.ReadAllLines(yamlFilePath))
            {
                string line = raw.Trim();
                int comment = line.IndexOf('#');
                if (comment >= 0)
                {
                    line = line.Substring(0, comment).Trim();
                }
                if (line.Length == 0 || line.StartsWith("//"))
                {
                    continue;
                }

                bool indented = raw.Length > 0 && (raw[0] == ' ' || raw[0] == '\t');
                if (!indented && line.EndsWith(":"))
                {
                    section = line.Substring(0, line.Length - 1).Trim();
                    continue;
                }

                int colon = line.IndexOf(':');
                if (colon < 0)
                {
                    continue;
                }
                string key = line.Substring(0, colon).Trim();
                string value = line.Substring(colon + 1).Trim().Trim('"');

                switch (section)
                {
                    case "scoreTransfer":
                        ApplyScoreTransfer(config.ScoreTransfer, key, value);
                        break;
                    case "dummyLogin":
                        ApplyDummyLogin(config.DummyLogin, key, value);
                        break;
                }
            }

            _config = config;
        }

        public ScoreTransferConfig ScoreTransfer => _config.ScoreTransfer;
        public DummyLoginConfig DummyLogin => _config.DummyLogin;

        /// <summary>当配置文件不存在时，生成一份带注释的默认配置。</summary>
        public static void WriteDefault(string path)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(path, DefaultYaml, new System.Text.UTF8Encoding(false));
        }

        private const string DefaultYaml =
            "# Sinmai-ScoreTransfer 配置\r\n" +
            "scoreTransfer: # 分数转移\r\n" +
            "  enable: true # 是否启用\r\n" +
            "  port: 8082 # 网页服务端口\r\n" +
            "  webEnable: true # 是否启动网页服务(若启动后游戏断网，请设为 false，可能是防火墙拦截)\r\n" +
            "  batchSize: 4 # 每个 Session 最多转移曲目数\r\n" +
            "  enterTimeoutSeconds: 120 # 等待进入选歌界面的超时(秒)\r\n" +
            "  trackTimeoutSeconds: 120 # 等待单曲完成的超时(秒)\r\n" +
            "  fillRemainingTracks: true # 最后一首后若本局还有剩余 Track，重复最后一首直到结算\r\n" +
            "  uploadTimeoutSeconds: 60 # 等待成绩提交(UpsertUserAll)成功的超时(秒)\r\n" +
            "  logoutTimeoutSeconds: 60 # 等待登出(UserLogout)成功的超时(秒)\r\n" +
            "  loginTimeoutSeconds: 120 # 等待回到可登录界面的超时(秒)\r\n" +
            "  minSessionSeconds: 120 # 保证从登录到登出(结算/上传)至少持续这么久，不够会先停留（服务器只在登出时接收上传）\r\n" +
            "  songWaitSeconds: 0 # 每首开始前额外等待多少秒（一般不需要，MinSession 已兜底）\r\n" +
            "  playWaitSeconds: 60 # 进入谱面后先正常游玩多久再强制结算（太短服务器会拒收，建议≥60）\r\n" +
            "  autoContinue: true # 一批(4首)打完后若有剩余曲目，自动在“继续游戏”界面选择继续（不登出，继续下一批）\r\n" +
            "dummyLogin: # 自动登录(刷卡)\r\n" +
            "  enable: true # 是否启用\r\n" +
            "  defaultUserId: 1 # 默认用户ID\r\n";

        private static void ApplyScoreTransfer(ScoreTransferConfig cfg, string key, string value)
        {
            switch (key)
            {
                case "enable": cfg.Enable = ParseBool(value); break;
                case "port": cfg.Port = ParseInt(value, cfg.Port); break;
                case "webEnable": cfg.WebEnable = ParseBool(value); break;
                case "batchSize": cfg.BatchSize = ParseInt(value, cfg.BatchSize); break;
                case "enterTimeoutSeconds": cfg.EnterTimeoutSeconds = ParseFloat(value, cfg.EnterTimeoutSeconds); break;
                case "trackTimeoutSeconds": cfg.TrackTimeoutSeconds = ParseFloat(value, cfg.TrackTimeoutSeconds); break;
                case "fillRemainingTracks": cfg.FillRemainingTracks = ParseBool(value); break;
                case "uploadTimeoutSeconds": cfg.UploadTimeoutSeconds = ParseFloat(value, cfg.UploadTimeoutSeconds); break;
                case "logoutTimeoutSeconds": cfg.LogoutTimeoutSeconds = ParseFloat(value, cfg.LogoutTimeoutSeconds); break;
                case "loginTimeoutSeconds": cfg.LoginTimeoutSeconds = ParseFloat(value, cfg.LoginTimeoutSeconds); break;
                case "minSessionSeconds": cfg.MinSessionSeconds = ParseFloat(value, cfg.MinSessionSeconds); break;
                case "songWaitSeconds": cfg.SongWaitSeconds = ParseFloat(value, cfg.SongWaitSeconds); break;
                case "playWaitSeconds": cfg.PlayWaitSeconds = ParseFloat(value, cfg.PlayWaitSeconds); break;
                case "autoContinue": cfg.AutoContinue = ParseBool(value); break;
            }
        }

        private static void ApplyDummyLogin(DummyLoginConfig cfg, string key, string value)
        {
            switch (key)
            {
                case "enable": cfg.Enable = ParseBool(value); break;
                case "defaultUserId": cfg.DefaultUserId = ParseInt(value, cfg.DefaultUserId); break;
            }
        }

        private static bool ParseBool(string value)
        {
            return value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value == "1"
                || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || value.Equals("on", StringComparison.OrdinalIgnoreCase);
        }

        private static int ParseInt(string value, int fallback)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result) ? result : fallback;
        }

        private static float ParseFloat(string value, float fallback)
        {
            return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float result) ? result : fallback;
        }
    }

    public class Config
    {
        public ScoreTransferConfig ScoreTransfer { get; set; }
        public DummyLoginConfig DummyLogin { get; set; }
    }

    public class ScoreTransferConfig
    {
        public bool Enable { get; set; }
        public int Port { get; set; } = 8082;
        public bool WebEnable { get; set; } = true;
        public int BatchSize { get; set; } = 4;
        public float EnterTimeoutSeconds { get; set; } = 120f;
        public float TrackTimeoutSeconds { get; set; } = 120f;
        public bool FillRemainingTracks { get; set; } = true;
        public float UploadTimeoutSeconds { get; set; } = 60f;
        public float LogoutTimeoutSeconds { get; set; } = 60f;
        public float LoginTimeoutSeconds { get; set; } = 120f;
        public float MinSessionSeconds { get; set; } = 120f;
        public float SongWaitSeconds { get; set; } = 0f;
        public float PlayWaitSeconds { get; set; } = 60f;
        public bool AutoContinue { get; set; } = true;
    }

    public class DummyLoginConfig
    {
        public bool Enable { get; set; }
        public int DefaultUserId { get; set; } = 1;
    }
}
