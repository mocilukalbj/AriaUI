# 桌面入口与实际 AOT 冒烟验证（2026-09-06）

本次针对当前工作树和桌面安装入口验证，不沿用阶段结项报告代替实测。原有 M01_NATIVE_COMPARISON.md、P01_NATIVE_PROBE.md 的未提交内容未修改。

## 桌面入口

- 桌面 `~/桌面/AriaUI.desktop` 链接到 `~/.local/share/applications/ariaui.desktop`。
- 检查前 Exec 指向 `publish/linux-x64/AriaUI`，其时间为 2026-09-03，属于旧产物，未包含 libaria2 随包依赖。
- 原 `publish/linux-x64-aot/` 为 2026-09-05 20:34 构建，也早于当晚最终修复。
- 已从当前源码重新生成 AOT，验证后替换 `publish/linux-x64-aot/`，并将桌面 Exec 改为该目录的 AriaUI。
- 原 AOT 保留在 `publish/linux-x64-aot.before-desktop-check-20260906/`；原桌面文件备份在 `/tmp/ariaui-desktop-check/ariaui.desktop.before`。
- `desktop-file-validate` 通过；隔离测试配置内 `gio launch` 桌面文件返回 0。启动器返回值只表示派发成功，程序运行证据见下文。
- 最终 AriaUI SHA256：`db32542165023790383e06aa8aa0409975d4ddd433be0d20677f0bc24c0f2577`。
- 校验清单已更新此次 AOT/native 项，其余旧发布目录未重新发布。

## 实际发现与修复

1. 初次启动当前源码的 AOT 后，经薄宿主成功下载测试文件；界面打开目录触发 `DirectoryNotFoundException: /tmp/desktop-smoke.bin`，应用退出码 134。
2. 根因为 NativeAriaEngineHost 用 `/tmp` 和 URL 猜测文件路径，快照未复制真实文件元数据。新增只读 C ABI 文件信息查询，由 owner 从 libaria2 复制目录、实际文件路径、大小、完成字节与状态，支持 UTF-8 和容量不足扩容，handle 由 RAII 释放。
3. ViewModel 打开目录传递父目录；预期文件系统/系统启动器错误转换为用户错误通知，避免从同步按钮命令逃逸而终止 UI。
4. 原 Native/C09 假定所有未暂停任务均 active，与 aria2 并发上限不符。改为验证真实分类、15 个指定暂停任务、5 个指定移除任务，以及 102 项分区完整性和唯一性。

## 验证结果

- 托管测试项目构建通过：0 警告、0 错误；修复后的 Native AOT 发布成功。
- 分组执行的 20 个相关用例最新结果均 Pass：Desktop/Paths、Fake/C09、Native/C01/C02/C06/C07/C09/C10/C11、A01–A05、U01、Step1/Wiring、E2E/Takeover、P02/NoAria2cDownload、F06、F07。
- Desktop/Paths 使用本地 HTTP、长目录、中文 out 文件名，验证实际文件/快照路径及大小一致，并直接执行 ViewModel 打开文件/目录命令及失败通知路径。
- 修复后的实际 AOT 桌面进程 + 独立 AriaUI.Host + Native Messaging stdio + Unix Socket 再次下载成功。
- 实际文件：262144 字节；SHA256 `2312394bd99545d9de131c24efb781e765ac1aec243f2ed9347597a793a415e9`；重复 requestId 返回同一 GID `6171c658db17f254`，没有再次添加。
- 同一启动监督进程读取 /proc，确认应用加载随包 libaria2.so.0 与 libaria2_bridge.so；该环境没有 aria2c 子进程，启动时没有应用持有的 TCP/UDP socket；网关为私有 Unix Socket。HTTP fixture 的本机端口属于测试源。
- `git diff --check` 通过。

## 测试边界与复现

实际 AOT 冒烟完成后已用 SIGTERM 结束核对过 PID 的临时实例；这一步是测试清理，不作为真实窗口正常关闭的证据。测试 profile、session、下载与日志位于 `/tmp/ariaui-desktop-check/`。使用 bubblewrap 将大小写两个历史配置目录映射到临时目录，保留真实用户配置与任务。首次普通沙箱测试因配置目录只读/本机监听受限失败；提供隔离配置、系统设备和必要执行权限后重测，未以环境错误作为产品失败。

测试入口：`dotnet run --project tests/AriaUI.Tests/AriaUI.Tests.csproj -- --filter Desktop/Paths`。该用例需本机 fixture 监听权限；应用服务相关用例仍应隔离实际 session 目录。

本次没有重新执行 24 小时压测或 1,000 次启停。当前桌面控制工具不提供原生窗口操作 API，因此未完成所有实际控件逐项点击与视觉验收；ViewModel 命令验证不能等同于完整视觉验收。真实 Chromium 扩展接管未实测：当前只有内置浏览器控制通道，检查的常见 Chromium 用户目录也未发现 com.ariaui.downloader 宿主注册清单。薄宿主/网关链路通过不代表浏览器扩展已安装。
