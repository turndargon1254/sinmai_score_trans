using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MAI2System;
using MelonLoader;
using SinmaiAssist.Cheat;
using SinmaiAssist.Fix;
using SinmaiAssist.GUI;
using SinmaiAssist.Utils;
using UnityEngine;

namespace SinmaiAssist
{
    public static partial class BuildInfo
    {
        public const string Name = "Sinmai-ScoreTransfer";
        public const string Description = "Sinmai Score Transfer";
        public const string Author = "WYH2004 & Error063";
        public const string Company = null;
        public const string Version = "1.0.0";
        public const string DownloadLink = null;
    }

    public class SinmaiAssist : MelonMod
    {
        private static bool isPatchFailed = false;
        public static ConfigManager config;
        public static string gameID = "Unknown";
        public static uint gameVersion = 00000;
        public static bool Flag1 = false;

        private bool _panelVisible;
        private bool _toggleKeyDown;

        public override void OnInitializeMelon()
        {
            config = new ConfigManager();

            string yamlFilePath = $"{BuildInfo.Name}/config.yml";
            if (File.Exists(yamlFilePath))
            {
                try
                {
                    config.Initialize(yamlFilePath);
                    MelonLogger.Msg("配置加载完成");
                }
                catch (Exception e)
                {
                    MelonLogger.Error($"配置解析失败，使用默认配置: {e}");
                }
            }
            else
            {
                MelonLogger.Warning($"未找到配置文件 \"{System.IO.Path.GetFullPath(yamlFilePath)}\"，使用默认配置 (enable=true, port=8082)。");
            }

            try
            {
                gameID = (string)typeof(ConstParameter).GetField("GameIDStr",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy).GetValue(null);
                gameVersion = (uint)typeof(ConstParameter).GetField("NowGameVersion",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy).GetValue(null);
            }
            catch (Exception e)
            {
                MelonLogger.Error("无法获取 GameID/GameVersion");
                MelonLogger.Error(e);
            }
            MelonLogger.Msg($"GameInfo: {gameID} {gameVersion}");

            var codes = new List<int> { 83, 68, 71, 66 };
            Flag1 = gameID.Equals(string.Concat(codes.Select(code => (char)code)));

            // 启动相关底层修复（环境检测/加密/版本检查等）——与分数转移无关，但 1.56 上可能需要。
            if (config.Fix != null && config.Fix.Enable)
            {
                if (config.Fix.DisableEnvironmentCheck) Patch(typeof(DisableEnvironmentCheck));
                if (config.Fix.DisableReboot) Patch(typeof(DisableReboot));
                if (config.Fix.FixCheckAuth) Patch(typeof(FixCheckAuth));
                if (config.Fix.SkipCakeHashCheck) Patch(typeof(SkipCakeHashCheck));
                if (config.Fix.SkipSpecialNumCheck) Patch(typeof(SkipSpecialNumCheck));
                if (config.Fix.SkipVersionCheck) Patch(typeof(SkipVersionCheck));
                if (config.Fix.DisableEncryption) Patch(typeof(DisableEncryption));
            }

            if (config.ScoreTransfer == null || !config.ScoreTransfer.Enable)
            {
                MelonLogger.Warning("分数转移未启用 (scoreTransfer.enable = false)，Mod 不加载任何功能。");
                return;
            }

            // 自动登录
            if (config.DummyLogin != null && config.DummyLogin.Enable)
            {
                DummyLoginState.IsChime = Flag1;
                DummyLoginState.DummyUserId = config.DummyLogin.DefaultUserId.ToString();
                if (Flag1)
                {
                    Patch(typeof(DummyChimeLogin));
                }
                else
                {
                    if (File.Exists("DEVICE/aime.txt"))
                    {
                        DummyLoginState.DummyLoginCode = File.ReadAllText("DEVICE/aime.txt").Trim();
                    }
                    Patch(typeof(DummyAimeLogin));
                }
                Patch(typeof(DummyLoginTicker));
            }

            // 分数转移核心
            Patch(typeof(AchievementSetter));
            Patch(typeof(ResultAdvancer));
            Patch(typeof(NetworkObserver));
            Patch(typeof(MusicSelect));
            Patch(typeof(ScoreTransfer));
            Patch(typeof(GameState));
            Patch(typeof(BatchTransfer));

            ScoreTransferHttpServer.Init(config.ScoreTransfer.Port);

            if (isPatchFailed)
            {
                MelonLogger.Warning("部分 Patch 失败，请确认使用与 Mod 匹配的游戏版本。");
            }
            MelonLogger.Msg("Sinmai-ScoreTransfer 加载完成");

            try
            {
                foreach (MelonMod mod in MelonHandler.Mods)
                {
                    MelonLogger.Msg($"[ScoreTransfer] 已加载 Mod: {mod.Info.Name} {mod.Info.Version}");
                }
                MelonLogger.Warning("[ScoreTransfer] 若同时安装 AquaMai 且其 Shim 报 MissingMethodException，请更新或禁用它（与 1.56 不兼容）。");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[ScoreTransfer] 枚举 Mod 失败: {e.Message}");
            }
        }

        public override void OnUpdate()
        {
            if (config != null && config.ScoreTransfer != null && config.ScoreTransfer.Enable)
            {
                SongDatabase.EnsureBuilt();
            }
        }

        public override void OnGUI()
        {
            if (Input.GetKeyDown(KeyCode.F8))
            {
                if (!_toggleKeyDown)
                {
                    _panelVisible = !_panelVisible;
                }
                _toggleKeyDown = true;
            }
            else
            {
                _toggleKeyDown = false;
            }

            if (_panelVisible)
            {
                ScoreTransferPanel.OnGUI();
            }
        }

        private static bool Patch(Type type)
        {
            try
            {
                MelonLogger.Msg($"- Patch: {type}");
                HarmonyLib.Harmony.CreateAndPatchAll(type);
                return true;
            }
            catch (Exception e)
            {
                MelonLogger.Error($"Patch {type} failed.");
                MelonLogger.Error(e.Message);
                MelonLogger.Error(e.StackTrace);
                isPatchFailed = true;
                return false;
            }
        }
    }
}
