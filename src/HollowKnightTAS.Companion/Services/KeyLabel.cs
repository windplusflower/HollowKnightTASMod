using System;

namespace HollowKnightTAS.Companion.Services
{
    // Display-only key names: symbols for punctuation, short names elsewhere.
    public static class KeyLabel
    {
        public static string Compact(string key) => key switch
        {
            "Left Arrow" or "LeftArrow" => "←",
            "Right Arrow" or "RightArrow" => "→",
            "Up Arrow" or "UpArrow" => "↑",
            "Down Arrow" or "DownArrow" => "↓",
            "Return" or "Enter" => "↵",
            "Escape" => "Esc",
            "Left Shift" or "LeftShift" => "LShift",
            "Right Shift" or "RightShift" => "RShift",
            "Left Control" or "LeftControl" => "LCtrl",
            "Right Control" or "RightControl" => "RCtrl",
            "Left Bracket" or "LeftBracket" => "[",
            "Right Bracket" or "RightBracket" => "]",
            "Backquote" or "BackQuote" => "`",
            "Minus" => "-",
            "Equals" => "=",
            "Backslash" => "\\",
            "Semicolon" => ";",
            "Quote" => "'",
            "Comma" => ",",
            "Period" => ".",
            "Slash" => "/",
            _ when key.Length == 6 && key.StartsWith("Alpha", StringComparison.Ordinal) => key.Substring(5),
            _ => key
        };
    }
}
