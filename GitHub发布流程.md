# 拾光・GitHub Releases 在线更新配置与发布流程

程序已内置在线更新功能（v1.0.0 起）：启动自动检查 + 设置弹窗「检查更新」按钮。

更新源（GitHub 仓库地址）**由程序内置、不可修改**，需在发布版本时于源码中配置一次。



***

## 一、创建 GitHub 仓库（首次）

1. 打开 [https://github.com](https://github.com) ，登录你的账号（没有则先注册）。
2. 右上角 **+ → New repository**（新建仓库），进入「创建一个新仓库」页面。
3. 页面分两步：「一般的」和「配置」，逐项填写如下：

### ① 一般的（基本信息）

| 填写项 | 怎么填 | 说明 |
|---|---|---|
| **所有者 \*** | 你的 GitHub 用户名（如 `t828`） | 下拉选择，默认是你自己 |
| **仓库名称 \*** | `PickLight` | 页面实时提示「PickLight 可用」即可继续 |
| **描述** | 可选，如「拾光：米哈游启动器缓存链接提取工具」 | 最长 350 字，不填也行 |

> 标记 \* 的为必填项；页面提示"好的仓库名称应该简短易记"——`PickLight` 即可，与程序名一致。

### ② 配置（仓库选项）

| 选项 | 选什么 | 说明（重要） |
|---|---|---|
| **选择可见性 \*** | **民众（Public）** | **必须选 Public**：更新检查走 GitHub 匿名 API，免费账号的私有仓库读不到 Releases，选 Private 用户端就检查不了更新 |
| **添加 README 文件** | 开启（在） | 建议开启，仓库自带简介页，内容随便写 |
| **添加 .gitignore** | 无（或选 C#） | 本项目实际发布的是编译好的 `PickLight.exe`；选「无」最省事，想规范一点可选 **C#**（自动忽略编译中间文件） |
| **添加许可证** | 无许可证（或选 MIT） | 自己用的工具选「无许可证」即可；若公开给他人使用，建议 **MIT License**（最宽松） |

4. 确认无误后点击底部绿色 **创建存储库（Create repository）** 完成。

> 记下你的 **GitHub 用户名 / 仓库名**，格式为 `你的用户名/PickLight`（如 `t828/PickLight`），下一步要填进源码。



***

## 二、在源码中配置更新源（一次性）

> ✅ **本项目已完成配置**：`UpdateRepoDefault = "t828/PickLight"`（仓库 https://github.com/t828/PickLight），当前编译的 exe 已带真实更新源，可直接发版。

（若换仓库才需要重做以下步骤：）

编辑 `E:\project\doubao\拾光\LinkExtractor.cs` 顶部：

```
const string UpdateRepoDefault = "t828/PickLight"; // 改为你的 GitHub 用户名/仓库名
```

然后**重新编译**（`build.ps1`），从这版 exe 开始，用户端才会真正检查到你的 Releases。



***

## 三、发布一个新版本（首次发布 v1.0.0，之后每次更新重复此步）

### 1. 编译当前源码



```
# 先关闭正在运行的 PickLight
Stop-Process -Name PickLight -Force -ErrorAction SilentlyContinue
# 再编译
powershell -NoProfile -ExecutionPolicy Bypass -File E:\project\doubao\拾光\build.ps1
```

产物：`E:\project\doubao\拾光\PickLight.exe`（发布时把 `PickLight.exe` 和 `ffplay.exe` 一起带上，用户需要两者共存）。

### 2. 创建 GitHub Release



* 打开你的仓库页面：`https://github.com/你的用户名/PickLight`

* 右侧 **Releases → Create a new release**（或 `releases` 标签页 → **Draft a new release**）

* 填写：


  * **Tag**：`v1.0.0`（**必须带 v 前缀、且大于当前程序内版本号才会提示更新**；后续依次 `v1.0.1`、`v1.1.0`……）

  * **Release title**：`v1.0.0`

  * **Describe this release**（更新说明）：写清楚这次改了什么，例如：



```
- 新增：在线更新（自动检查 + 手动检查）
- 新增：图片详情信息展示
- 优化：翻页不再闪烁
```



* 附件区 **Attach binaries**：拖入 `PickLight.exe`

* 点击 **Publish release**

> 版本对比规则：程序内 
>
> `AppVersion="1.0.0"`
>
> ，只有 GitHub 最新 tag（去掉 v 后）比它大才提示更新。

### 3. 用户端更新流程

用户打开程序后：



* 启动时若勾选了「启动时自动检查更新」，1.5 秒后静默检查；发现新版本弹窗提示

* 也可在 **设置 → 检查更新** 手动检查

* 点击 **立即更新** → 程序自动下载新 exe → 提示重启 → 自动替换并重启（无需手动安装）



***

## 四、日常更新发布清单（每次发版照此执行）



| 步骤 | 操作                                             |
| -- | ---------------------------------------------- |
| 1  | 修改功能 / 修复 bug（改 `LinkExtractor.cs`）            |
| 2  | 如需改版本号：`AppVersion = "1.0.x"`（改成比上次大的数字）       |
| 3  | 编译 `build.ps1`                                 |
| 4  | GitHub → Releases → New release，Tag 写 `v1.0.x` |
| 5  | 附上 `PickLight.exe`，写更新说明，Publish               |
| 6  | （可选）自己先运行旧版点「检查更新」验证能发现新版并成功升级                 |



***

## 五、注意事项



* **版本号必须单调递增**：新 tag 比旧 tag 大才生效（1.0.1 > 1.0.0；1.0.10 > 1.0.9）。

* **exe 名**：Release 里附件的 exe 文件名随意，程序会取第一个 `.exe` 附件下载。

* **自动更新开关**：用户可在 设置 里关闭「启动时自动检查更新」，关闭后手动「检查更新」仍可用；开关状态保存在 settings.ini（`AutoUpdate=0`）。

* **更新源不可改**：设置弹窗里更新源是只读状态；用户无法通过配置文件篡改更新地址。

* 网络要求：检查更新需能访问 `api.github.com`（中国大陆网络通常可直连，个别网络环境需代理）。