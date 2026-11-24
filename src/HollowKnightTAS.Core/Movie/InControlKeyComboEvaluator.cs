using System;
using System.Collections.Generic;

namespace HollowKnightTAS.Core.Movie
{
    /// <summary>
    /// Pure-logic evaluator for InControl <c>Key</c> and <c>KeyCombo</c> bindings against
    /// Unity <c>KeyCode</c> integer values, with no dependency on Unity or InControl assemblies.
    /// </summary>
    /// <remarks>
    /// Mapping matches <c>InControl.Key</c> (0..131) and <c>InControl.UnityKeyboardProvider.KeyMappings</c>
    /// in Hollow Knight's <c>Assembly-CSharp.dll</c>.
    /// </remarks>
    public static class InControlKeyComboEvaluator
    {
        private const int InControlKeyNone = 0;
        private const int InControlKeyShift = 1;
        private const int InControlKeyAlt = 2;
        private const int InControlKeyCommand = 3;
        private const int InControlKeyControl = 4;

        private const short KeyCodeNone = 0;
        private const short KeyCodeRightShift = 303;
        private const short KeyCodeLeftShift = 304;
        private const short KeyCodeRightControl = 305;
        private const short KeyCodeLeftControl = 306;
        private const short KeyCodeRightAlt = 307;
        private const short KeyCodeLeftAlt = 308;
        private const short KeyCodeRightCommand = 309;
        private const short KeyCodeLeftCommand = 310;

        // Indexed directly by (int)InControl.Key (0..131).
        // Generic modifier keys (1..4) map to two KeyCodes and are handled explicitly in IsInControlKeyHeld.
        private static readonly short[] SingleKeyMappings =
        {
            KeyCodeNone,         // 0: None
            KeyCodeNone,         // 1: Shift (LeftShift || RightShift)
            KeyCodeNone,         // 2: Alt (LeftAlt || RightAlt)
            KeyCodeNone,         // 3: Command (LeftCommand || RightCommand)
            KeyCodeNone,         // 4: Control (LeftControl || RightControl)
            KeyCodeLeftShift,    // 5: LeftShift
            KeyCodeLeftAlt,      // 6: LeftAlt
            KeyCodeLeftCommand,  // 7: LeftCommand
            KeyCodeLeftControl,  // 8: LeftControl
            KeyCodeRightShift,   // 9: RightShift
            KeyCodeRightAlt,     // 10: RightAlt
            KeyCodeRightCommand, // 11: RightCommand
            KeyCodeRightControl, // 12: RightControl
            27,                  // 13: Escape
            282,                 // 14: F1
            283,                 // 15: F2
            284,                 // 16: F3
            285,                 // 17: F4
            286,                 // 18: F5
            287,                 // 19: F6
            288,                 // 20: F7
            289,                 // 21: F8
            290,                 // 22: F9
            291,                 // 23: F10
            292,                 // 24: F11
            293,                 // 25: F12
            48,                  // 26: Key0 (Alpha0)
            49,                  // 27: Key1 (Alpha1)
            50,                  // 28: Key2 (Alpha2)
            51,                  // 29: Key3 (Alpha3)
            52,                  // 30: Key4 (Alpha4)
            53,                  // 31: Key5 (Alpha5)
            54,                  // 32: Key6 (Alpha6)
            55,                  // 33: Key7 (Alpha7)
            56,                  // 34: Key8 (Alpha8)
            57,                  // 35: Key9 (Alpha9)
            97,                  // 36: A
            98,                  // 37: B
            99,                  // 38: C
            100,                 // 39: D
            101,                 // 40: E
            102,                 // 41: F
            103,                 // 42: G
            104,                 // 43: H
            105,                 // 44: I
            106,                 // 45: J
            107,                 // 46: K
            108,                 // 47: L
            109,                 // 48: M
            110,                 // 49: N
            111,                 // 50: O
            112,                 // 51: P
            113,                 // 52: Q
            114,                 // 53: R
            115,                 // 54: S
            116,                 // 55: T
            117,                 // 56: U
            118,                 // 57: V
            119,                 // 58: W
            120,                 // 59: X
            121,                 // 60: Y
            122,                 // 61: Z
            96,                  // 62: Backquote (BackQuote)
            45,                  // 63: Minus
            61,                  // 64: Equals
            8,                   // 65: Backspace
            9,                   // 66: Tab
            91,                  // 67: LeftBracket
            93,                  // 68: RightBracket
            92,                  // 69: Backslash
            59,                  // 70: Semicolon
            39,                  // 71: Quote
            13,                  // 72: Return
            44,                  // 73: Comma
            46,                  // 74: Period
            47,                  // 75: Slash
            32,                  // 76: Space
            277,                 // 77: Insert
            127,                 // 78: Delete
            278,                 // 79: Home
            279,                 // 80: End
            280,                 // 81: PageUp
            281,                 // 82: PageDown
            276,                 // 83: LeftArrow
            275,                 // 84: RightArrow
            273,                 // 85: UpArrow
            274,                 // 86: DownArrow
            256,                 // 87: Pad0 (Keypad0)
            257,                 // 88: Pad1 (Keypad1)
            258,                 // 89: Pad2 (Keypad2)
            259,                 // 90: Pad3 (Keypad3)
            260,                 // 91: Pad4 (Keypad4)
            261,                 // 92: Pad5 (Keypad5)
            262,                 // 93: Pad6 (Keypad6)
            263,                 // 94: Pad7 (Keypad7)
            264,                 // 95: Pad8 (Keypad8)
            265,                 // 96: Pad9 (Keypad9)
            300,                 // 97: Numlock
            267,                 // 98: PadDivide (KeypadDivide)
            268,                 // 99: PadMultiply (KeypadMultiply)
            269,                 // 100: PadMinus (KeypadMinus)
            270,                 // 101: PadPlus (KeypadPlus)
            271,                 // 102: PadEnter (KeypadEnter)
            266,                 // 103: PadPeriod (KeypadPeriod)
            12,                  // 104: Clear
            272,                 // 105: PadEquals (KeypadEquals)
            294,                 // 106: F13
            295,                 // 107: F14
            296,                 // 108: F15
            313,                 // 109: AltGr
            301,                 // 110: CapsLock
            33,                  // 111: ExclamationMark (Exclaim)
            126,                 // 112: Tilde
            64,                  // 113: At
            35,                  // 114: Hash
            36,                  // 115: Dollar
            37,                  // 116: Percent
            94,                  // 117: Caret
            38,                  // 118: Ampersand
            42,                  // 119: Asterisk
            40,                  // 120: LeftParen
            41,                  // 121: RightParen
            95,                  // 122: Underscore
            43,                  // 123: Plus
            123,                 // 124: LeftBrace (LeftCurlyBracket)
            125,                 // 125: RightBrace (RightCurlyBracket)
            124,                 // 126: Pipe
            58,                  // 127: Colon
            34,                  // 128: DoubleQuote
            60,                  // 129: LessThan (Less)
            62,                  // 130: GreaterThan (Greater)
            63,                  // 131: QuestionMark (Question)
        };

        /// <summary>
        /// Evaluates whether the specified <c>InControl.Key</c> integer value is currently held
        /// according to <paramref name="isKeyCodeHeld"/>.
        /// </summary>
        public static bool IsInControlKeyHeld(int inControlKey, Func<short, bool> isKeyCodeHeld)
        {
            if (isKeyCodeHeld == null) throw new ArgumentNullException(nameof(isKeyCodeHeld));
            if (inControlKey <= InControlKeyNone || inControlKey >= SingleKeyMappings.Length)
                return false;

            switch (inControlKey)
            {
                case InControlKeyShift:
                    return isKeyCodeHeld(KeyCodeLeftShift) || isKeyCodeHeld(KeyCodeRightShift);
                case InControlKeyAlt:
                    return isKeyCodeHeld(KeyCodeLeftAlt) || isKeyCodeHeld(KeyCodeRightAlt);
                case InControlKeyCommand:
                    return isKeyCodeHeld(KeyCodeLeftCommand) || isKeyCodeHeld(KeyCodeRightCommand);
                case InControlKeyControl:
                    return isKeyCodeHeld(KeyCodeLeftControl) || isKeyCodeHeld(KeyCodeRightControl);
                default:
                    var mapped = SingleKeyMappings[inControlKey];
                    return mapped != KeyCodeNone && isKeyCodeHeld(mapped);
            }
        }

        /// <summary>
        /// Evaluates an InControl <c>KeyCombo</c>: returns <c>true</c> iff <paramref name="includes"/>
        /// is non-empty, every key in <paramref name="includes"/> is held, and no key in
        /// <paramref name="excludes"/> is held.
        /// </summary>
        public static bool EvaluateCombo(
            IReadOnlyList<int> includes,
            IReadOnlyList<int> excludes,
            Func<short, bool> isKeyCodeHeld)
        {
            if (includes == null) throw new ArgumentNullException(nameof(includes));
            if (excludes == null) throw new ArgumentNullException(nameof(excludes));
            if (isKeyCodeHeld == null) throw new ArgumentNullException(nameof(isKeyCodeHeld));

            if (includes.Count == 0)
                return false;

            for (var i = 0; i < includes.Count; i++)
            {
                if (!IsInControlKeyHeld(includes[i], isKeyCodeHeld))
                    return false;
            }

            for (var i = 0; i < excludes.Count; i++)
            {
                if (IsInControlKeyHeld(excludes[i], isKeyCodeHeld))
                    return false;
            }

            return true;
        }
    }
}
