# Glink 技术契约（智能体协作规范）

## 项目定位
个人灵感积累库。存放文本/音频/图片/视频，卡片即内容本身，无多余按钮。

## 技术栈（不得更改）
- WinUI 3 / Windows App SDK 2.5.1，net10.0-windows，C#，命名空间 `Glink`
- 存储：Microsoft.Data.Sqlite（数据库 moodia.db）+ 文件沙盒（`%LOCALAPPDATA%\Glink\media\`，按 Guid 存原始文件）
- 包版本：Microsoft.Data.Sqlite 9.0.x

## 数据模型（Glink.Core/Glink.Core/Models.cs，单文件）
```csharp
namespace Glink.Core;
public enum ItemType { Text, Image, Audio, Video }
public class MoodCategory { public Guid Id; public string Name; public string AccentHex; public int SortOrder; }
public class Item {
  public Guid Id; public ItemType Type; public string? Content;      // 文本内容
  public string? FilePath;                                           // 媒体相对路径 media\xxx.ext
  public string? FileName; public long SizeBytes;
  public Guid? CategoryId; public DateTime CreatedAt; public string? Note;
  public bool IsFavorite;
}
```

## 服务层（Glink.Core/Store.cs，单文件，静态类 Store）
- `static async Task InitializeAsync()` — 建库建表，首次运行创建默认分类"全部灵感"(AccentHex=#E8956B)
- 分类：`Task<List<MoodCategory>> GetCategoriesAsync()` / `AddCategoryAsync(name, accentHex)` / `RenameCategoryAsync(id, name)` / `DeleteCategoryAsync(id)`（其下条目变为未分类）/ `GetItemCountAsync(id)` 返回该分类条目数（含 "all" 与 "uncategorized" 约定值）
- 条目：`Task<List<Item>> GetItemsAsync(Guid? categoryId, ItemType? type, string? timeFilter, bool favOnly)` — timeFilter ∈ null/"today"/"week"/"earlier"，按 CreatedAt DESC
- `Task<Item> AddFileAsync(StorageFile file, Guid? categoryId)` — 拷入沙盒，推断类型（按扩展名）
- `Task<Item> AddTextAsync(string text, Guid? categoryId)`
- `Task UpdateAsync(Item item)` / `DeleteAsync(Guid id)`（同时删沙盒文件）/ `ToggleFavoriteAsync(id)`
- `string AbsolutePath(string relPath)` — 沙盒根 = `%LOCALAPPDATA%\Glink\`

## UI 契约（Glink/MainWindow.xaml，单页面实现）
布局参照用户截图：
- 左侧 260px 深色 (#141414) 侧栏：顶部应用名 "Glink" + 副标题；"全部灵感"导航项（带计数）；下方 "情绪意向" 标签 + 彩色圆点分类列表（右侧计数）；底部一个 "+" 按钮（新建分类）
- 右侧内容区 (#0D0D0D)：顶部大标题（当前分类名，前置彩色圆点）+ 副标题（条目数）；右上角橙色圆角主按钮（+ 添加灵感）；其下筛选行：左侧类型 chips（全部/文本/图片/音频/视频，可折叠"更多"省略）、右侧时间 chips（全部/今日/近7天/更早）
- 卡片瀑布流（2 列以上自适应 WrapGrid 或 StaggeredPanel 风格）：深色圆角容器 #1C1C1C，容器内只有内容本身：
  - 文本：直接显示文字
  - 图片：直接显示 BitmapImage（解码到 ~800px 节流）
  - 音频：极简波形条 + 点击卡片任意处播放/暂停（MediaElement/ MediaPlayer，播放时波形动画）
  - 视频：封面帧（缩略图）+ 点击播放
- 交互直觉化：右键卡片 → MenuFlyout 仅 4 项（收藏/移动到分类/删除/复制文本）；悬停卡片轻微上浮 + 边框微亮
- 添加：点击主按钮 → ContentDialog + 按钮（选择文本/文件）——用 Flyout 弹出小面板更佳
- 新建/重命名分类：ContentDialog 输入名字；颜色从固定 8 色板自动轮换
- 动画：纯色、克制（透明度/位移 150-250ms，Composition 或 Storyboard），不使用渐变彩虹

## 主题色
背景 #0D0D0D / 侧栏 #141414 / 卡片 #1C1C1C / 边框 #2A2A2A / 文字 #EDEDED / 次要 #8A8A8A / 主色橙 #E8956B
字体：Segoe UI Variable Display（标题）/ Segoe UI（正文）

## 文件归属
- Glink.Core/Glink.Core.csproj + Models.cs + Store.cs → 后端智能体
- Glink/MainWindow.xaml(.cs)、App.xaml（主题资源）、App.xaml.cs → 前端智能体（删除模板 MainPage）
- Glink.csproj 由集成阶段负责加 ProjectReference 与包
- 禁止触碰对方文件；禁止输出多余 README
