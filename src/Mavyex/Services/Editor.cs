namespace Mavyex.Services;

// STUB – editor.rs port. Keep these signatures.

/// <summary>F4 external editor: command templates ({file} {dir} {line}) + per-extension rules.</summary>
public static class Editor
{
    public const string Default = "notepad {file}";
    /// <summary>The command template for a file: first matching rule ("ext1,ext2 = cmd", * = any), else the default.</summary>
    public static string CommandFor(string defaultCmd, string rules, string file) => string.IsNullOrWhiteSpace(defaultCmd) ? Default : defaultCmd;
    public static List<string> SplitArgs(string s) => [s];
    public static List<string> Build(string template, string file) => [];
    /// <summary>Starts the editor (resolves .cmd/.exe on PATH); throws with a readable message on failure.</summary>
    public static void Launch(string template, string file) => throw new NotImplementedException();
}
