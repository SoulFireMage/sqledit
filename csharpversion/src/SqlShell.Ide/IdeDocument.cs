using System.Text;

namespace SqlShell.Ide;

/// <summary>Editor document state: path, saved text, and dirty tracking. Mirrors the Python editor.</summary>
public sealed class IdeDocument
{
    public string? Path { get; private set; }

    public string SavedText { get; private set; } = string.Empty;

    public string DisplayName => Path is null ? "Untitled.sql" : Path;

    public bool IsDirty(string currentText) => currentText != SavedText;

    public void Reset()
    {
        Path = null;
        SavedText = string.Empty;
    }

    /// <summary>Load a UTF-8 (optionally BOM-prefixed) SQL file.</summary>
    public void Load(string path)
    {
        SavedText = File.ReadAllText(path, Encoding.UTF8);
        Path = path;
    }

    public void Save(string text, string path)
    {
        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Path = path;
        SavedText = text;
    }
}
