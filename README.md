# ezViewer · 便携式漫画阅读器

C# / WPF（.NET 8）编写的单文件漫画阅读器。发布产物只有一个 `ezViewer.exe`（自包含 .NET 运行库），拷贝到任意 Windows 10/11 x64 电脑即可运行，不需要安装任何东西。

## 功能

**列表模式**：打开一个文件夹（「打开文件夹」/ `Ctrl+O` / 拖放 / 命令行参数），左侧列出其中的子文件夹和漫画。

- `↑` `↓` 在列表中切换，`←` `→` 翻页
- `Alt+↑` 退到父目录，`Alt+↓` 进入选中的子目录（也可以用 `Backspace` / `Enter`，或双击列表项）
- 也可以直接打开压缩包、PDF 或单张图片，会自动列出它所在文件夹的内容并定位

**自由模式（书库）**：在左侧收藏夹中添加目录，点击后以封面网格展示其中（包括所有子目录）的漫画。

- 「顺序 / 随机」：选随机时封面网格仍保持原顺序，只打乱进入阅读后左侧列表的顺序（点开的那本排第一）；「随机读一本」
- 按名称搜索（空格分隔多个关键词，同时匹配所在子目录名）
- 封面缩略图有磁盘缓存，第二次进入时直接显示
- 在封面网格或阅读界面左侧的漫画列表上按下鼠标中键，再上下移动鼠标即可快速自动滚动

**阅读**

- 长边适配：等比缩放，整页完整显示在窗口中（小图会放大）
- `PageUp` / `PageDown` 放大 / 缩小，放大后 `W` `A` `S` `D` 移动画面（按住 `Shift` 加速），翻页后自动恢复长边适配
- 底部可拖动的进度条，显示当前页 / 总页数
- 末页继续往后翻自动进入下一本；首页继续往前翻进入上一本（停在它的最后一页）。按住方向键不会连续跨本
- 全屏（`F11` 或双击画面）：无边框铺满当前显示器，进出时没有窗口缩放动画；全屏时底栏自动隐藏，鼠标移到底部时出现
- 鼠标：滚轮翻页，`Ctrl+滚轮` 以指针为中心缩放，左键拖动画面
- 预加载相邻页面，翻页无等待
- 动图（GIF、WebP、APNG）翻到时自动播放，按文件里记录的每帧时长和循环次数；放大、拖动时照常播放，翻页后停止

## 支持的格式

| 类型 | 格式 |
| --- | --- |
| 图片文件夹 | jpg、png、bmp、gif、tif、webp、avif、heic、jxl 等（解码使用 Windows 自带的 WIC，webp/avif/heic/jxl 需要系统装有对应的图像扩展） |
| 动图 | gif、webp、apng（.png / .apng），在文件夹和压缩包里都能播放 |
| 压缩包 | zip/cbz、rar/cbr（RAR4/RAR5，含固实压缩）、7z/cb7（含固实压缩）、tar/cbt |
| PDF | 内置 PDFium 渲染 |

压缩包里没有 UTF-8 标记的文件名（zip、旧版 RAR、tar），会用整个包的文件名一起判断编码：简体中文 GBK、日文 Shift-JIS、韩文 CP949、繁体中文 Big5，或者不带标记的 UTF-8（macOS 打的包；分解形式的假名、韩文会合并成正常显示的字符）。

一本“漫画”指：直接包含图片的文件夹、一个压缩包或一个 PDF。页面按文件名自然排序（`第2话` 在 `第10话` 之前），与资源管理器一致。

## 快捷键

| 按键 | 作用 |
| --- | --- |
| `←` / `→` | 上一页 / 下一页 |
| `↑` / `↓` | 列表中的上一项 / 下一项 |
| `Alt+↑`、`Backspace` | 退到父目录 |
| `Alt+↓`、`Enter`、双击列表项 | 进入选中的子目录 |
| `Home` / `End` | 第一页 / 最后一页 |
| `PageUp` / `PageDown` | 放大 / 缩小 |
| `W` `A` `S` `D` | 放大后移动画面（`Shift` 加速） |
| `Tab` | 显示 / 隐藏漫画列表 |
| `F11`、双击 | 全屏 |
| 鼠标中键 | 在封面网格 / 漫画列表上自动滚动：按住拖动松开即停；单击一下则持续滚动，再点一下、滚动滚轮或按 `Esc` 停止 |
| `Esc` | 退出全屏 / 返回书库 |
| `Ctrl+O` / `Ctrl+Shift+O` | 打开文件夹 / 打开文件 |
| `Ctrl+F` / `F5` | 书库搜索 / 重新扫描 |
| `F1` | 快捷键帮助 |

### 文件夹导航

- 列表里的项目：直接含图片的文件夹、压缩包、PDF 是可以阅读的漫画；不直接含图片、只有子文件夹或压缩包的是“文件夹”（带文件夹图标），选中时右侧显示它包含多少个文件夹和漫画。带 `›` 的项目可以进入。所在文件夹自己也有图片时，第一项显示为“××（本文件夹）”。
- `Alt+↑` 列出上一级目录，并选中刚才所在的文件夹；正在读的漫画保持打开，可以接着翻页。连续 `Alt+↓` 会沿原路回到这本漫画所在的位置。一直退到磁盘根目录为止。
- `Alt+↓` 进入选中的文件夹，自动打开其中第一本漫画（全是子文件夹时选中第一个）。只有图片、没有子文件夹的漫画不能再进入。
- 末页 / 首页继续翻页跨本时会跳过文件夹，只在漫画之间切换。
- 自由模式下按 `Alt+↑` 会切换到当前漫画所在文件夹的列表（相当于从书库进入列表模式），阅读不中断；返回书库后封面和随机顺序都不受影响。

## 数据存放位置

| 内容 | 位置 |
| --- | --- |
| 设置（收藏夹、窗口位置等） | exe 同目录的 `ezViewer.json`；该目录不可写时改用 `%AppData%\ezViewer\` |
| 封面缓存 | `%LocalAppData%\ezViewer\thumbs\`（超过 1 GB 时自动清理最旧的） |
| 错误日志 | `%LocalAppData%\ezViewer\error.log` |

单文件版首次运行时，.NET 会把内置的原生库（WPF 图形库、PDFium）解压到 `%TEMP%\.net\ezViewer\`，之后启动直接复用。

## 构建

需要 .NET 8 SDK（或更高版本的 SDK）。

```powershell
# 发布单文件版到 dist\ezViewer.exe
powershell -ExecutionPolicy Bypass -File build.ps1

# 发布前先运行自测（生成测试漫画库，验证扫描、排序、各格式读取、文件名编码、动图合成、封面、文件夹跳转等）
powershell -ExecutionPolicy Bypass -File build.ps1 -Test

# 指定版本号（写入 exe 的文件版本，默认取 ezViewer.csproj 中的 Version）
powershell -ExecutionPolicy Bypass -File build.ps1 -Version 1.2.0
```

调试运行：`dotnet run --project src\ezViewer`。

### 自动构建与发布

GitHub Actions（`.github/workflows/build.yml`）在推送到 `main` 和提交 PR 时构建单文件版，可以在运行记录的 Artifacts 中下载 `ezViewer.exe`。推送 `v` 开头的版本标签时，构建完成后自动创建 GitHub Release 并附上 `ezViewer.exe`：

```powershell
git tag v1.2.0
git push origin v1.2.0
```

- 版本号取自标签（`v1.2.0` → `1.2.0`）并写入 exe 的文件版本，不需要修改 `ezViewer.csproj`。
- 带后缀的标签（如 `v1.2.0-beta.1`）发布为预发行版。
- CI 中不运行自测：自测的预期结果基于简体中文系统（列表按拼音排序；生成测试数据时 tar 要在 GBK 代码页下才能写入中文文件名），GitHub 的 Windows 构建机是英文系统。打标签前请先在本地运行 `build.ps1 -Test`。

## 项目结构

```
src/ezViewer/
  Core/        漫画扫描、自然排序、文件类型、设置
  Sources/     数据源：文件夹、压缩包（SharpCompress，含文件名编码判断）、PDF（PDFium P/Invoke）
  Imaging/     后台图片解码、缩略图编码、动图逐帧解码与合成
  Reader/      页面预加载器
  Library/     封面缩略图服务（内存 LRU + 磁盘缓存）
  Views/       书库界面、阅读界面、动图播放
  Themes/      深色主题样式
tests/ezViewer.SelfTest/     自测程序
tools/make-icon.ps1          生成程序图标
.github/workflows/build.yml  自动构建与发布
```

## 第三方组件

- [SharpCompress](https://github.com/adamhathcock/sharpcompress)（MIT）：读取 zip/rar/7z/tar
- [PDFium](https://pdfium.googlesource.com/pdfium/)（BSD-3-Clause / Apache-2.0），二进制来自 [bblanchon/pdfium-binaries](https://github.com/bblanchon/pdfium-binaries)

## 已知限制

- 不支持带密码的压缩包和加密 PDF。
- 压缩包文件名编码是按文字特征推断的：包里只有一两个字的极短文件名时偶尔会判断错（显示乱码），不影响阅读。
- 动图画布超过 4096×4096 时只显示第一帧；所有帧合计超过 128 MB 时不缓存，边解码边播放。WebP 动图依赖系统的 WebP 图像扩展（Windows 11 自带）。
- .NET 8 的官方支持将于 2026-11-10 结束。程序自带运行库，到期后照常可用；如需升级，把 `ezViewer.csproj` 和自测工程中的 `net8.0-windows` 改为 `net10.0-windows`、`.github/workflows/build.yml` 中的 `dotnet-version` 改为 `10.0.x`，并使用 .NET 10 SDK 构建即可（.NET 10 需要 Visual Studio 2026 才能在 VS 中打开）。
