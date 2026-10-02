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
            DummyLogin = new DummyLoginConfig(),
            Fix = new FixConfig()
        };

        public void Initialize(string yamlFilePath)
        {
            Config config = new Config
            {
                ScoreTransfer = new ScoreTransferConfig(),
                DummyLogin = new DummyLoginConfig(),
                Fix = new FixConfig()
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
                    case "fix":
                        ApplyFix(config.Fix, key, value);
                        break;
                }
            }

            _config = config;
        }

        public ScoreTransferConfig ScoreTransfer => _config.ScoreTransfer;
        public DummyLoginConfig DummyLogin => _config.DummyLogin;
        public FixConfig Fix => _config.Fix;

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
            "  batchSize: 4 # 每个 Session 最多转移曲目数\r\n" +
            "  enterTimeoutSeconds: 120 # 等待进入选歌界面的超时(秒)\r\n" +
            "  trackTimeoutSeconds: 120 # 等待单曲完成的超时(秒)\r\n" +
            "  fillRemainingTracks: true # 最后一首后若本局还有剩余 Track，重复最后一首直到结算\r\n" +
            "  uploadTimeoutSeconds: 60 # 等待成绩提交(UpsertUserAll)成功的超时(秒)\r\n" +
            "  logoutTimeoutSeconds: 60 # 等待登出(UserLogout)成功的超时(秒)\r\n" +
            "  loginTimeoutSeconds: 120 # 等待回到可登录界面的超时(秒)\r\n" +
            "dummyLogin: # 自动登录(刷卡)\r\n" +
            "  enable: true # 是否启用\r\n" +
            "  defaultUserId: 1 # 默认用户ID\r\n" +
            "fix: # 启动/联网相关修复\r\n" +
            "  enable: true # 是否启用\r\n" +
            "  disableEnvironmentCheck: true # 禁用运行环境检查(WarningProcess)\r\n" +
            "  disableEncryption: false # 禁用加密(官方服必须 false)\r\n" +
            "  disableReboot: false # 禁用维护/自动重启\r\n" +
            "  fixCheckAuth: false # 修复 CheckAuth(官方服必须 false)\r\n" +
            "  skipCakeHashCheck: false # 跳过 Cake.dll Hash 检查(官方服必须 false)\r\n" +
            "  skipSpecialNumCheck: false # 跳过特殊数字检查(官方服必须 false)\r\n" +
            "  skipVersionCheck: false # 登录时跳过版本检查(官方服必须 false)\r\n";

        private static void ApplyScoreTransfer(ScoreTransferConfig cfg, string key, string value)
        {
            switch (key)
            {
                case "enable": cfg.Enable = ParseBool(value); break;
                case "port": cfg.Port = ParseInt(value, cfg.Port); break;
                case "batchSize": cfg.BatchSize = ParseInt(value, cfg.BatchSize); break;
                case "enterTimeoutSeconds": cfg.EnterTimeoutSeconds = ParseFloat(value, cfg.EnterTimeoutSeconds); break;
                case "trackTimeoutSeconds": cfg.TrackTimeoutSeconds = ParseFloat(value, cfg.TrackTimeoutSeconds); break;
                case "fillRemainingTracks": cfg.FillRemainingTracks = ParseBool(value); break;
                case "uploadTimeoutSeconds": cfg.UploadTimeoutSeconds = ParseFloat(value, cfg.UploadTimeoutSeconds); break;
                case "logoutTimeoutSeconds": cfg.LogoutTimeoutSeconds = ParseFloat(value, cfg.LogoutTimeoutSeconds); break;
                case "loginTimeoutSeconds": cfg.LoginTimeoutSeconds = ParseFloat(value, cfg.LoginTimeoutSeconds); break;
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

        private static void ApplyFix(FixConfig cfg, string key, string value)
        {
            switch (key)
            {
                case "enable": cfg.Enable = ParseBool(value); break;
                case "disableEnvironmentCheck": cfg.DisableEnvironmentCheck = ParseBool(value); break;
                case "disableEncryption": cfg.DisableEncryption = ParseBool(value); break;
                case "disableReboot": cfg.DisableReboot = ParseBool(value); break;
                case "fixCheckAuth": cfg.FixCheckAuth = ParseBool(value); break;
                case "skipCakeHashCheck": cfg.SkipCakeHashCheck = ParseBool(value); break;
                case "skipSpecialNumCheck": cfg.SkipSpecialNumCheck = ParseBool(value); break;
                case "skipVersionCheck": cfg.SkipVersionCheck = ParseBool(value); break;
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
        public FixConfig Fix { get; set; }
    }

    public class ScoreTransferConfig
    {
        public bool Enable { get; set; }
        public int Port { get; set; } = 8082;
        public int BatchSize { get; set; } = 4;
        public float EnterTimeoutSeconds { get; set; } = 120f;
        public float TrackTimeoutSeconds { get; set; } = 120f;
        public bool FillRemainingTracks { get; set; } = true;
        public float UploadTimeoutSeconds { get; set; } = 60f;
        public float LogoutTimeoutSeconds { get; set; } = 60f;
        public float LoginTimeoutSeconds { get; set; } = 120f;
    }

    public class DummyLoginConfig
    {
        public bool Enable { get; set; }
        public int DefaultUserId { get; set; } = 1;
    }

    public class FixConfig
    {
        public bool Enable { get; set; } = true;
        public bool DisableEnvironmentCheck { get; set; } = true;
        public bool DisableEncryption { get; set; } = false;
        public bool DisableReboot { get; set; } = false;
        public bool FixCheckAuth { get; set; } = false;
        public bool SkipCakeHashCheck { get; set; } = false;
        public bool SkipSpecialNumCheck { get; set; } = false;
        public bool SkipVersionCheck { get; set; } = false;
    }
}
