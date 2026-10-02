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

        private static void ApplyScoreTransfer(ScoreTransferConfig cfg, string key, string value)
        {
            switch (key)
            {
                case "enable": cfg.Enable = ParseBool(value); break;
                case "port": cfg.Port = ParseInt(value, cfg.Port); break;
                case "batchSize": cfg.BatchSize = ParseInt(value, cfg.BatchSize); break;
                case "enterTimeoutSeconds": cfg.EnterTimeoutSeconds = ParseFloat(value, cfg.EnterTimeoutSeconds); break;
                case "trackTimeoutSeconds": cfg.TrackTimeoutSeconds = ParseFloat(value, cfg.TrackTimeoutSeconds); break;
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
        public int BatchSize { get; set; } = 4;
        public float EnterTimeoutSeconds { get; set; } = 120f;
        public float TrackTimeoutSeconds { get; set; } = 120f;
    }

    public class DummyLoginConfig
    {
        public bool Enable { get; set; }
        public int DefaultUserId { get; set; } = 1;
    }
}
