using Glink.Core;
using Windows.System;

namespace Glink;

internal readonly record struct KeyGesture(VirtualKey Key, bool Ctrl, bool Shift, bool Alt)
{
    public string Display
    {
        get
        {
            var parts = new List<string>(4);
            if (Ctrl) parts.Add("Ctrl");
            if (Shift) parts.Add("Shift");
            if (Alt) parts.Add("Alt");
            parts.Add(KeyName(Key));
            return string.Join(" + ", parts);
        }
    }

    public bool Matches(VirtualKey key, bool ctrl, bool shift, bool alt) =>
        SameKey(Key, key) && Ctrl == ctrl && Shift == shift && Alt == alt;

    // OEM plus/minus and the numpad twins press the same way on most layouts.
    private static VirtualKey Canonical(VirtualKey key) => key switch
    {
        VirtualKey.Add => (VirtualKey)187,
        VirtualKey.Subtract => (VirtualKey)189,
        VirtualKey.NumberPad0 => VirtualKey.Number0,
        _ => key
    };

    public static bool SameKey(VirtualKey a, VirtualKey b) => Canonical(a) == Canonical(b);

    public static string KeyName(VirtualKey key)
    {
        if (key >= VirtualKey.Number0 && key <= VirtualKey.Number9)
            return ((int)key - (int)VirtualKey.Number0).ToString();
        if (key >= VirtualKey.NumberPad0 && key <= VirtualKey.NumberPad9)
            return ((int)key - (int)VirtualKey.NumberPad0).ToString();
        if (key >= VirtualKey.A && key <= VirtualKey.Z)
            return ((char)('A' + (int)key - (int)VirtualKey.A)).ToString();
        return key switch
        {
            (VirtualKey)187 or VirtualKey.Add => "+",
            (VirtualKey)189 or VirtualKey.Subtract => "-",
            (VirtualKey)188 => ",",
            (VirtualKey)190 => ".",
            (VirtualKey)191 or VirtualKey.Divide => "/",
            (VirtualKey)186 => ";",
            (VirtualKey)192 => "`",
            VirtualKey.Space => "空格",
            VirtualKey.Up => "↑",
            VirtualKey.Down => "↓",
            VirtualKey.Left => "←",
            VirtualKey.Right => "→",
            VirtualKey.Escape => "Esc",
            _ => key.ToString()
        };
    }
}

internal sealed record ShortcutCommand(string Id, string Name, KeyGesture Default);

internal static class ShortcutService
{
    public static IReadOnlyList<ShortcutCommand> Commands { get; } = new[]
    {
        new ShortcutCommand("undo", "撤回", new KeyGesture(VirtualKey.Z, true, false, false)),
        new ShortcutCommand("redo", "重做", new KeyGesture(VirtualKey.Y, true, false, false)),
        new ShortcutCommand("newText", "新建文字", new KeyGesture(VirtualKey.N, true, false, false)),
        new ShortcutCommand("addFile", "添加文件", new KeyGesture(VirtualKey.O, true, false, false)),
        new ShortcutCommand("paste", "粘贴导入", new KeyGesture(VirtualKey.V, true, false, false)),
        new ShortcutCommand("newCategory", "新建分类", new KeyGesture(VirtualKey.N, true, true, false)),
        new ShortcutCommand("viewAll", "查看全部灵感", new KeyGesture(VirtualKey.Number1, true, false, false)),
        new ShortcutCommand("viewFavorites", "查看收藏", new KeyGesture(VirtualKey.Number2, true, false, false)),
        new ShortcutCommand("pack", "紧凑平铺", new KeyGesture(VirtualKey.P, true, true, false)),
        new ShortcutCommand("settings", "打开设置", new KeyGesture((VirtualKey)188, true, false, false)),
        new ShortcutCommand("zoomIn", "放大画布", new KeyGesture((VirtualKey)187, true, false, false)),
        new ShortcutCommand("zoomOut", "缩小画布", new KeyGesture((VirtualKey)189, true, false, false)),
        new ShortcutCommand("zoomReset", "恢复 100% 缩放", new KeyGesture(VirtualKey.Number0, true, false, false)),
    };

    private static readonly Dictionary<string, KeyGesture> Defaults =
        Commands.ToDictionary(c => c.Id, c => c.Default);

    private static readonly Dictionary<string, KeyGesture> Overrides = new(StringComparer.Ordinal);

    public static event Action? Changed;

    // Hand-rolled line format "id|key|ctrl|shift|alt": reflection-based JSON
    // serialization is disabled by Release trimming, which used to crash the app.
    private static string SettingsPath => Path.Combine(Store.DataDirectory, "shortcuts.txt");

    public static KeyGesture GestureOf(string id) =>
        Overrides.TryGetValue(id, out var gesture) ? gesture : Defaults.GetValueOrDefault(id);

    public static bool IsCustomized(string id) => Overrides.ContainsKey(id);

    public static ShortcutCommand? FindConflict(KeyGesture gesture, string exceptId) =>
        Commands.FirstOrDefault(c => c.Id != exceptId &&
            GestureOf(c.Id).Matches(gesture.Key, gesture.Ctrl, gesture.Shift, gesture.Alt));

    public static void SetOverride(string id, KeyGesture gesture)
    {
        Overrides[id] = gesture;
        Save();
        Changed?.Invoke();
    }

    public static void ResetAll()
    {
        if (Overrides.Count == 0) return;
        Overrides.Clear();
        Save();
        Changed?.Invoke();
    }

    public static void Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            foreach (var line in File.ReadAllLines(SettingsPath))
            {
                var parts = line.Split('|');
                if (parts.Length != 5 || !Defaults.ContainsKey(parts[0])) continue;
                if (!int.TryParse(parts[1], out var key) ||
                    !bool.TryParse(parts[2], out var ctrl) ||
                    !bool.TryParse(parts[3], out var shift) ||
                    !bool.TryParse(parts[4], out var alt)) continue;
                var gesture = new KeyGesture((VirtualKey)key, ctrl, shift, alt);
                if (gesture.Key == VirtualKey.None || FindConflict(gesture, parts[0]) != null) continue;
                Overrides[parts[0]] = gesture;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Fall back to defaults; a broken file must not block startup.
        }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Store.DataDirectory);
            var lines = Overrides.Select(pair =>
                $"{pair.Key}|{(int)pair.Value.Key}|{pair.Value.Ctrl}|{pair.Value.Shift}|{pair.Value.Alt}");
            var temporary = SettingsPath + ".tmp";
            File.WriteAllLines(temporary, lines);
            File.Move(temporary, SettingsPath, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Customizations stay in memory for this session if the disk write fails.
        }
    }
}
