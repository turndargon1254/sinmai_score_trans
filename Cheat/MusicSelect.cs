using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MAI2.Util;
using Manager;
using MelonLoader;
using Monitor;
using Process;
using Process.SubSequence;
using SinmaiAssist.Types;
using Type = System.Type;

namespace SinmaiAssist.Cheat
{
    /// <summary>
    /// 移植自 SongSearch (SongSearchBot.MusicSelect) 的歌曲定位/切换逻辑。
    /// 只负责把选歌界面的光标移动到指定曲目，并可在设置完选曲信息后直接开始游戏。
    /// </summary>
    public static class MusicSelect
    {
        public static List<ReadOnlyCollection<MusicSelectProcess.CombineMusicSelectData>> CombineMusicDataList;
        public static MusicSelectProcess Process;

        private static SequenceBase[][] _subSequenceArray;
        private static MusicSelectProcess.SubSequence[] _currentPlayerSubSequence;
        private static MusicSelectProcess.SubSequence[] _beforePlayerSubSequence;

        public static bool IsReady
        {
            get
            {
                if (Process == null)
                {
                    return false;
                }
                return CombineMusicDataList != null || Process.CombineMusicDataList != null;
            }
        }

        private static List<ReadOnlyCollection<MusicSelectProcess.CombineMusicSelectData>> Data
        {
            get
            {
                if (CombineMusicDataList != null)
                {
                    return CombineMusicDataList;
                }
                return Process?.CombineMusicDataList;
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(MusicSelectProcess), "OnStart")]
        public static void OnStart(
            MusicSelectProcess __instance,
            List<ReadOnlyCollection<MusicSelectProcess.CombineMusicSelectData>> ____combineMusicDataList,
            SequenceBase[][] ____subSequenceArray,
            MusicSelectProcess.SubSequence[] ____currentPlayerSubSequence,
            MusicSelectProcess.SubSequence[] ____beforePlayerSubSequence)
        {
            CombineMusicDataList = ____combineMusicDataList ?? __instance.CombineMusicDataList;
            Process = __instance;
            _subSequenceArray = ____subSequenceArray;
            _currentPlayerSubSequence = ____currentPlayerSubSequence;
            _beforePlayerSubSequence = ____beforePlayerSubSequence;

            try
            {
                ScoreTransfer.SongList = BuildSongList(____combineMusicDataList);
                MelonLogger.Msg($"[ScoreTransfer] 已缓存 {ScoreTransfer.SongList.Count} 首可转移歌曲");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[ScoreTransfer] 构建歌曲列表失败: {e}");
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(MusicSelectProcess), "OnRelease")]
        public static void OnRelease()
        {
            CombineMusicDataList = null;
            Process = null;
            _subSequenceArray = null;
            _currentPlayerSubSequence = null;
            _beforePlayerSubSequence = null;
        }

        /// <summary>
        /// 把选歌界面光标移动到指定曲目。id 为含 DX(>=10000) 的完整 id。
        /// </summary>
        public static string SelectMusic(int id)
        {
            List<ReadOnlyCollection<MusicSelectProcess.CombineMusicSelectData>> list = Data;
            if (list == null)
            {
                return "当前状态不能选择歌曲";
            }

            for (int i = 0; i < list.Count; i++)
            {
                ReadOnlyCollection<MusicSelectProcess.CombineMusicSelectData> category = list[i];
                MusicSelectProcess.CombineMusicSelectData data = category.FirstOrDefault(it => it.msDetailData.musicId == id);
                if (data == null)
                {
                    data = category.FirstOrDefault(it => it.msDetailData.musicId == id % 10000);
                }

                if (data != null)
                {
                    int index = list[i].IndexOf(data);
                    Process.CurrentCategorySelect = i;
                    Process.CurrentMusicSelect = index;
                    Process.ScoreType = (MAI2System.ConstParameter.ScoreKind)(id < 10000 ? 0 : 1);
                    Process.ChangeBGM();

                    for (int j = 0; j < Process.MonitorArray.Length; j++)
                    {
                        if (Singleton<UserDataManager>.Instance.GetUserData(j).IsEntry)
                        {
                            MusicSelectMonitor monitor = Process.MonitorArray[j];
                            monitor.SetScrollMusicCard(false);
                            monitor.SetScrollGenreCard(false);
                            monitor.OutGenreTab();
                            monitor.StartCoroutine(NextFrame(j));
                        }
                    }
                    return "成功";
                }
            }
            return "找不到这首歌";
        }

        private static IEnumerator NextFrame(int playerIndex)
        {
            yield return null;
            _subSequenceArray[playerIndex][(int)_currentPlayerSubSequence[playerIndex]].Reset();
            _beforePlayerSubSequence[playerIndex] = _currentPlayerSubSequence[playerIndex];
            _currentPlayerSubSequence[playerIndex] = (MusicSelectProcess.SubSequence)3;
            _subSequenceArray[playerIndex][3].OnStartSequence();
            yield break;
        }

        /// <summary>
        /// 复用游戏自身的 MusicSelectProcess.GameStart 直接开始游戏。
        /// </summary>
        public static string StartGame()
        {
            if (Process == null)
            {
                return "当前状态不能开始游戏";
            }

            try
            {
                MethodInfo method = typeof(MusicSelectProcess).GetMethod(
                    "GameStart",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (method == null)
                {
                    return "找不到 GameStart 方法";
                }

                method.Invoke(Process, null);
                return "成功";
            }
            catch (Exception e)
            {
                return "开始游戏失败: " + e.Message;
            }
        }

        private static List<TransferSongInfo> BuildSongList(
            List<ReadOnlyCollection<MusicSelectProcess.CombineMusicSelectData>> list)
        {
            List<TransferSongInfo> result = new List<TransferSongInfo>();
            HashSet<int> seen = new HashSet<int>();
            if (list == null)
            {
                return result;
            }

            foreach (ReadOnlyCollection<MusicSelectProcess.CombineMusicSelectData> category in list)
            {
                if (category == null)
                {
                    continue;
                }

                foreach (MusicSelectProcess.CombineMusicSelectData combine in category)
                {
                    if (combine == null)
                    {
                        continue;
                    }

                    try
                    {
                        int id = combine.msDetailData.musicId;
                        if (id <= 0 || seen.Contains(id))
                        {
                            continue;
                        }

                        int scoreType = id >= 10000 ? 1 : 0;
                        object selectData = combine.musicSelectData[scoreType];
                        if (selectData == null)
                        {
                            selectData = combine.musicSelectData[0] ?? combine.musicSelectData[1];
                        }
                        if (selectData == null)
                        {
                            continue;
                        }

                        object musicData = GetMember(selectData, "MusicData");
                        if (musicData == null)
                        {
                            continue;
                        }

                        seen.Add(id);
                        TransferSongInfo info = new TransferSongInfo
                        {
                            id = id,
                            scoreType = scoreType,
                            name = GetLocalized(musicData, "name"),
                            artist = GetLocalized(musicData, "artist"),
                            charts = BuildCharts(musicData)
                        };
                        result.Add(info);
                    }
                    catch
                    {
                        // 跳过无法解析的条目
                    }
                }
            }

            return result;
        }

        private static TransferChartInfo[] BuildCharts(object musicData)
        {
            TransferChartInfo[] charts = new TransferChartInfo[5];
            object notesData = GetMember(musicData, "notesData");
            for (int d = 0; d < 5; d++)
            {
                TransferChartInfo chart = new TransferChartInfo { enable = false };
                try
                {
                    if (notesData is Array array && d < array.Length)
                    {
                        object notes = array.GetValue(d);
                        if (notes != null)
                        {
                            chart.enable = GetBool(notes, "isEnable");
                            chart.level = GetInt(notes, "level");
                            chart.levelDecimal = GetInt(notes, "levelDecimal");
                            chart.designer = GetLocalized(notes, "notesDesigner");
                        }
                    }
                }
                catch
                {
                    // ignore
                }
                charts[d] = chart;
            }
            return charts;
        }

        private static object GetMember(object obj, string name)
        {
            if (obj == null)
            {
                return null;
            }
            Type type = obj.GetType();
            PropertyInfo prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (prop != null)
            {
                return prop.GetValue(obj, null);
            }
            FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            return field?.GetValue(obj);
        }

        private static string GetLocalized(object obj, string name)
        {
            object value = GetMember(obj, name);
            if (value == null)
            {
                return "";
            }
            if (value is string str)
            {
                return str;
            }
            object localized = GetMember(value, "str");
            return localized?.ToString() ?? value.ToString() ?? "";
        }

        private static int GetInt(object obj, string name)
        {
            object value = GetMember(obj, name);
            if (value == null)
            {
                return 0;
            }
            try
            {
                return Convert.ToInt32(value);
            }
            catch
            {
                return 0;
            }
        }

        private static bool GetBool(object obj, string name)
        {
            object value = GetMember(obj, name);
            if (value == null)
            {
                return false;
            }
            try
            {
                return Convert.ToBoolean(value);
            }
            catch
            {
                return false;
            }
        }
    }
}
