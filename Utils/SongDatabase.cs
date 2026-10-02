using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using MAI2.Util;
using Manager;
using MelonLoader;
using SinmaiAssist.Types;
using Type = System.Type;

namespace SinmaiAssist.Utils
{
    /// <summary>
    /// 与选曲界面无关的歌曲数据库：直接从 DataManager/NotesListManager 构建，
    /// 游戏启动后即可用，供网页搜索使用（不依赖 MusicSelectProcess 是否已缓存）。
    /// 必须在主线程调用 <see cref="EnsureBuilt"/>；HTTP 线程只读 <see cref="Songs"/>。
    /// </summary>
    public static class SongDatabase
    {
        private static volatile List<TransferSongInfo> _songs = new List<TransferSongInfo>();
        private static volatile bool _built;
        private static bool _buildFailed;

        public static List<TransferSongInfo> Songs => _songs;
        public static int Count => _songs.Count;

        /// <summary>主线程调用：数据加载完成后构建一次。</summary>
        public static void EnsureBuilt()
        {
            if (_built || _buildFailed)
            {
                return;
            }
            try
            {
                List<TransferSongInfo> list = Build();
                if (list.Count > 0)
                {
                    _songs = list;
                    _built = true;
                    MelonLogger.Msg($"[Search] database built: {list.Count}");
                }
            }
            catch (Exception e)
            {
                _buildFailed = true;
                MelonLogger.Error($"[Search] database build failed: {e}");
            }
        }

        /// <summary>HTTP 线程调用：只读缓存，不触碰 Unity 对象。</summary>
        public static List<TransferSongInfo> Search(string query, int limit)
        {
            string q = (query ?? "").Trim();
            List<TransferSongInfo> result = new List<TransferSongInfo>();
            List<TransferSongInfo> source = _songs;
            foreach (TransferSongInfo song in source)
            {
                if (q.Length == 0 || Matches(song, q))
                {
                    result.Add(song);
                    if (limit > 0 && result.Count >= limit)
                    {
                        break;
                    }
                }
            }
            MelonLogger.Msg($"[Search] query=\"{q}\" database_count={source.Count} result_count={result.Count}");
            return result;
        }

        private static bool Matches(TransferSongInfo song, string q)
        {
            if (!string.IsNullOrEmpty(song.name) && song.name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
            if (!string.IsNullOrEmpty(song.artist) && song.artist.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
            if (song.id.ToString().IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
            foreach (TransferChartInfo chart in song.charts)
            {
                if (chart != null && !string.IsNullOrEmpty(chart.designer) &&
                    chart.designer.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        private static List<TransferSongInfo> Build()
        {
            List<TransferSongInfo> result = new List<TransferSongInfo>();
            HashSet<int> seen = new HashSet<int>();
            DataManager dataManager = Singleton<DataManager>.Instance;

            IEnumerable musics = dataManager.GetMusics() as IEnumerable;
            if (musics != null)
            {
                foreach (object item in musics)
                {
                    object musicData = UnwrapValue(item);
                    AddMusic(result, seen, musicData);
                }
            }

            if (result.Count == 0)
            {
                // 兜底：用 NotesListManager 的 key 逐个取 MusicData
                IEnumerable notes = Singleton<NotesListManager>.Instance.GetNotesList() as IEnumerable;
                if (notes != null)
                {
                    foreach (object item in notes)
                    {
                        object key = UnwrapKey(item);
                        if (key is int id)
                        {
                            AddMusic(result, seen, dataManager.GetMusic(id));
                        }
                    }
                }
            }

            return result;
        }

        private static void AddMusic(List<TransferSongInfo> result, HashSet<int> seen, object musicData)
        {
            if (musicData == null)
            {
                return;
            }
            try
            {
                int id = GetInt(musicData, "GetID");
                if (id <= 0 || id >= 100000 || seen.Contains(id))
                {
                    return;
                }
                seen.Add(id);

                TransferSongInfo info = new TransferSongInfo
                {
                    id = id,
                    scoreType = id >= 10000 ? 1 : 0,
                    name = GetStringId(musicData, "name"),
                    artist = GetStringId(musicData, "artistName"),
                    charts = BuildCharts(GetMember(musicData, "notesData"))
                };
                result.Add(info);
            }
            catch
            {
                // ignore
            }
        }

        private static TransferChartInfo[] BuildCharts(object notesData)
        {
            TransferChartInfo[] charts = new TransferChartInfo[5];
            for (int d = 0; d < 5; d++)
            {
                charts[d] = new TransferChartInfo { enable = false };
            }
            if (notesData is IEnumerable enumerable)
            {
                int d = 0;
                foreach (object notes in enumerable)
                {
                    if (d >= 5)
                    {
                        break;
                    }
                    try
                    {
                        charts[d] = new TransferChartInfo
                        {
                            enable = GetBool(notes, "isEnable"),
                            level = GetInt(notes, "level"),
                            levelDecimal = GetInt(notes, "levelDecimal"),
                            designer = GetStringId(notes, "notesDesigner")
                        };
                    }
                    catch
                    {
                        // ignore
                    }
                    d++;
                }
            }
            return charts;
        }

        private static object UnwrapValue(object item)
        {
            if (item == null)
            {
                return null;
            }
            Type t = item.GetType();
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
            {
                return t.GetProperty("Value").GetValue(item, null);
            }
            return item;
        }

        private static object UnwrapKey(object item)
        {
            if (item == null)
            {
                return null;
            }
            Type t = item.GetType();
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
            {
                return t.GetProperty("Key").GetValue(item, null);
            }
            return item;
        }

        private static string GetStringId(object obj, string member)
        {
            object value = GetMember(obj, member);
            if (value == null)
            {
                return "";
            }
            if (value is string s)
            {
                return s;
            }
            object str = GetMember(value, "str");
            return str?.ToString() ?? value.ToString() ?? "";
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

        private static int GetInt(object obj, string name)
        {
            if (obj == null)
            {
                return 0;
            }
            Type type = obj.GetType();
            PropertyInfo prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (prop != null && prop.PropertyType == typeof(int))
            {
                return (int)prop.GetValue(obj, null);
            }
            FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (field != null && field.FieldType == typeof(int))
            {
                return (int)field.GetValue(obj);
            }
            if (type.GetMethod(name, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null) is MethodInfo method &&
                method.ReturnType == typeof(int))
            {
                return (int)method.Invoke(obj, null);
            }
            return 0;
        }

        private static bool GetBool(object obj, string name)
        {
            object value = GetMember(obj, name);
            try
            {
                return value != null && Convert.ToBoolean(value);
            }
            catch
            {
                return false;
            }
        }
    }
}
