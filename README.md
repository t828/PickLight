# 拾光 (PickLight)

从米哈游启动器缓存文件（`data_*/f_*`）中提取媒体链接的桌面小工具。WPF 单文件程序，免安装，拖入缓存文件即可提取。

## 功能特性

- **多种导入方式**：选择文件夹 / 选择文件 / 直接拖入，自动去重（按 URL）
- **智能筛选**：类型 / 来源 / 记录 三列筛选 + 关键词搜索（时间、类型、来源、地址、备注均可搜）
- **行内操作**：每条记录可直接预览、复制链接、下载资源
- **预览弹窗**：图片可缩放 / 拖动 / 双击复位，棋盘格透明底；webm 等视频内嵌播放
- **记录管理**：分组管理（重命名 / 删除 / 合并），备注支持双击编辑
- **图片详情**：分辨率、格式、大小、色彩位深、DPI、EXIF 元数据展示（有数据才显示）
- **在线更新**：启动自动检查 + 手动检查，GitHub Releases 一键升级，无需手动安装
- **设置记忆**：窗口位置、大小、自动更新开关持久化保存；跨分辨率自适应

## 使用方法

1. 从 [Releases](https://github.com/t828/PickLight/releases) 下载最新版 `PickLight.exe`（需与 `ffplay.exe` 同目录）
2. 打开程序，把米哈游启动器的缓存文件（`data_*`、`f_*`）拖入窗口，或点「选择文件夹 / 选择文件」
3. 在列表中找到目标链接：预览查看、复制地址、或直接下载

## 数据说明

- 记录保存在程序目录下的 `links_history.txt`（tab 分隔，含日期 / 类型 / 来源 / URL / 备注 / 记录 / 导入时间）
- 本地数据不联网上传，仅"检查更新"时访问 GitHub API

## 在线更新

- 程序内置更新源为 `t828/PickLight`（GitHub Releases）
- 每次发布新版：在 Releases 创建 `vX.Y.Z` 标签并附带 `PickLight.exe`，用户端检查到更高版本即可一键更新

## 构建

需要 `.NET Framework 4.0` 的 `csc` 编译器：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1
```

产物：`PickLight.exe`

## 许可证

MIT License（详见仓库 LICENSE 文件）
