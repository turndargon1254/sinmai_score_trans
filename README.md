# Sinmai-ScoreTransfer (舞萌分数转移工具)

一个只做一件事的 MelonLoader Mod：**自动完成 Sinmai（舞萌 DX）的分数转移。**

给定「目标歌曲 / 目标难度 / 目标完成度（0.0000% ~ 101.0000%）」或一份待转移成绩列表后，
程序自动完成：分批（每批最多 4 首）→ 自动登录 → 自动进入选曲 → 切换到目标曲目 →
进入 Play → 写入指定完成度 → 完成本曲 → 下一首 → 每批结束自动重新进入，直到全部完成。

> 本 Mod 是原 SongSearch 与 FastSkip 功能的整合与精简版，仅保留分数转移所需代码。

## 使用

1. 将 `Sinmai-ScoreTransfer.dll` 放入游戏的 `Mods` 目录。
2. 将 `config.yml` 放入 `游戏目录/Sinmai-ScoreTransfer/`。
3. 启动游戏，停留在任意界面；用手机/浏览器打开 `http://<本机IP>:8082/`。
4. 搜索并选择歌曲，设置难度 / 谱面类型(SD/DX) / 完成度，点击「转移」；
   或将多首成绩填入「批量转移」后点击「开始批量」。
5. 游戏内按 `F8` 可打开状态面板（进度 / 失败重试 / 停止）。

## 配置（config.yml）

```yaml
scoreTransfer:
  enable: true            # 是否启用
  port: 8082              # 网页服务端口
  batchSize: 4            # 每批转移曲目数（舞萌一次游玩上限）
  enterTimeoutSeconds: 120 # 等待进入选歌界面的超时(秒)
  trackTimeoutSeconds: 120 # 等待单曲完成的超时(秒)
dummyLogin:
  enable: true            # 自动登录（刷卡/Chime）
  defaultUserId: 1        # 默认用户ID
```

## 开发 / 编译

1. 安装 .NET Framework 4.7.2 Developer Pack。
2. 将游戏依赖 DLL 放入 `Libs/`（`Assembly-CSharp.dll`、`0Harmony.dll`、`MelonLoader.dll`、
   `AMDaemon.NET.dll`、`ChimeLib.NET.dll`、`UnityEngine*.dll` 等）。
3. 执行 `dotnet build Sinmai-ScoreTransfer.sln -c Release` 或用 MSBuild 构建。
4. 输出位于 `Output/Sinmai-ScoreTransfer.dll`。

也可使用 GitHub Actions：`.github/workflows/build.yml`（需要仓库 Secret `OUTPUT_URL` 提供游戏依赖压缩包）。

## 输出文件

- `Mods/Sinmai-ScoreTransfer.dll`
- `Sinmai-ScoreTransfer/config.yml`

（除 Mod 本体外不产生其他 DLL）
