using System;
using System.Collections.Generic;
using HollowKnightTAS.Core.Movie;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.Movie;

[TestClass]
public class InControlKeyComboEvaluatorTests
{
    private const int KeyNone = 0;
    private const int KeyShift = 1;
    private const int KeyAlt = 2;
    private const int KeyCommand = 3;
    private const int KeyControl = 4;
    private const int KeyLeftShift = 5;
    private const int KeyLeftAlt = 6;
    private const int KeyLeftControl = 8;
    private const int KeyRightShift = 9;
    private const int KeyRightAlt = 10;
    private const int KeyRightControl = 12;
    private const int KeyEscape = 13;
    private const int KeyF1 = 14;
    private const int KeyF12 = 25;
    private const int Key0 = 26;
    private const int Key9 = 35;
    private const int KeyA = 36;
    private const int KeyZ = 61;
    private const int KeyBackquote = 62;
    private const int KeyBackspace = 65;
    private const int KeyTab = 66;
    private const int KeyReturn = 72;
    private const int KeySpace = 76;
    private const int KeyDelete = 78;
    private const int KeyLeftArrow = 83;
    private const int KeyDownArrow = 86;
    private const int KeyPad0 = 87;
    private const int KeyPad9 = 96;
    private const int KeyPadDivide = 98;
    private const int KeyPadEquals = 105;
    private const int KeyF13 = 106;
    private const int KeyF15 = 108;
    private const int KeyCapsLock = 110;
    private const int KeyQuestionMark = 131;

    [TestMethod]
    public void MapsSingleKeysAccurately()
    {
        var expected = new Dictionary<int, short>
        {
            [KeyEscape] = 27,
            [KeyF1] = 282,
            [KeyF1 + 4] = 286,   // F5
            [KeyF12] = 293,
            [KeyF13] = 294,
            [KeyF15] = 296,
            [Key0] = 48,
            [Key0 + 5] = 53,    // Key5
            [Key9] = 57,
            [KeyA] = 97,
            [KeyA + 12] = 109,  // M
            [KeyZ] = 122,
            [KeyBackquote] = 96,
            [KeyBackspace] = 8,
            [KeyTab] = 9,
            [KeyReturn] = 13,
            [KeySpace] = 32,
            [KeyDelete] = 127,
            [KeyLeftArrow] = 276,
            [KeyLeftArrow + 1] = 275, // RightArrow
            [KeyLeftArrow + 2] = 273, // UpArrow
            [KeyDownArrow] = 274,
            [KeyPad0] = 256,
            [KeyPad9] = 265,
            [KeyPadDivide] = 267,
            [KeyPadEquals] = 272,
            [KeyCapsLock] = 301,
            [KeyQuestionMark] = 63,
        };

        foreach (var pair in expected)
        {
            Assert.IsTrue(
                InControlKeyComboEvaluator.IsInControlKeyHeld(pair.Key, code => code == pair.Value),
                $"Expected InControl.Key({pair.Key}) to match KeyCode({pair.Value}).");
            Assert.IsFalse(
                InControlKeyComboEvaluator.IsInControlKeyHeld(pair.Key, _ => false),
                $"Expected InControl.Key({pair.Key}) to return false when no key is held.");
        }
    }

    [TestMethod]
    public void NoneAndOutOfRangeKeysReturnFalse()
    {
        Assert.IsFalse(InControlKeyComboEvaluator.IsInControlKeyHeld(KeyNone, _ => true));
        Assert.IsFalse(InControlKeyComboEvaluator.IsInControlKeyHeld(-1, _ => true));
        Assert.IsFalse(InControlKeyComboEvaluator.IsInControlKeyHeld(132, _ => true));
        Assert.IsFalse(InControlKeyComboEvaluator.IsInControlKeyHeld(999, _ => true));
    }

    [TestMethod]
    public void GenericModifiersMatchEitherLeftOrRightSide()
    {
        // Shift -> LeftShift (304) || RightShift (303)
        Assert.IsTrue(InControlKeyComboEvaluator.IsInControlKeyHeld(KeyShift, c => c == 304));
        Assert.IsTrue(InControlKeyComboEvaluator.IsInControlKeyHeld(KeyShift, c => c == 303));
        Assert.IsFalse(InControlKeyComboEvaluator.IsInControlKeyHeld(KeyShift, c => c == 306));

        // Alt -> LeftAlt (308) || RightAlt (307)
        Assert.IsTrue(InControlKeyComboEvaluator.IsInControlKeyHeld(KeyAlt, c => c == 308));
        Assert.IsTrue(InControlKeyComboEvaluator.IsInControlKeyHeld(KeyAlt, c => c == 307));
        Assert.IsFalse(InControlKeyComboEvaluator.IsInControlKeyHeld(KeyAlt, c => c == 304));

        // Command -> LeftCommand (310) || RightCommand (309)
        Assert.IsTrue(InControlKeyComboEvaluator.IsInControlKeyHeld(KeyCommand, c => c == 310));
        Assert.IsTrue(InControlKeyComboEvaluator.IsInControlKeyHeld(KeyCommand, c => c == 309));

        // Control -> LeftControl (306) || RightControl (305)
        Assert.IsTrue(InControlKeyComboEvaluator.IsInControlKeyHeld(KeyControl, c => c == 306));
        Assert.IsTrue(InControlKeyComboEvaluator.IsInControlKeyHeld(KeyControl, c => c == 305));
        Assert.IsFalse(InControlKeyComboEvaluator.IsInControlKeyHeld(KeyControl, c => c == 308));

        // Side-specific modifiers do not match opposite side
        Assert.IsTrue(InControlKeyComboEvaluator.IsInControlKeyHeld(KeyLeftShift, c => c == 304));
        Assert.IsFalse(InControlKeyComboEvaluator.IsInControlKeyHeld(KeyLeftShift, c => c == 303));
        Assert.IsTrue(InControlKeyComboEvaluator.IsInControlKeyHeld(KeyRightShift, c => c == 303));
        Assert.IsFalse(InControlKeyComboEvaluator.IsInControlKeyHeld(KeyRightShift, c => c == 304));
        Assert.IsTrue(InControlKeyComboEvaluator.IsInControlKeyHeld(KeyLeftAlt, c => c == 308));
        Assert.IsFalse(InControlKeyComboEvaluator.IsInControlKeyHeld(KeyRightAlt, c => c == 308));
        Assert.IsTrue(InControlKeyComboEvaluator.IsInControlKeyHeld(KeyLeftControl, c => c == 306));
        Assert.IsFalse(InControlKeyComboEvaluator.IsInControlKeyHeld(KeyRightControl, c => c == 306));
    }

    [TestMethod]
    public void EvaluateComboEnforcesIncludesAndExcludes()
    {
        // Empty includes must never trigger
        Assert.IsFalse(InControlKeyComboEvaluator.EvaluateCombo(Array.Empty<int>(), Array.Empty<int>(), _ => true));

        var ctrlShiftAIncludes = new[] { KeyControl, KeyShift, KeyA };
        var plainAIncludes = new[] { KeyA };
        var plainAExcludes = new[] { KeyControl, KeyShift };

        // Holding LeftControl + RightShift + A triggers Ctrl+Shift+A, does NOT trigger A-without-modifiers
        var heldCtrlShiftA = new HashSet<short> { 306, 303, 97 };
        Assert.IsTrue(InControlKeyComboEvaluator.EvaluateCombo(ctrlShiftAIncludes, Array.Empty<int>(), heldCtrlShiftA.Contains));
        Assert.IsFalse(InControlKeyComboEvaluator.EvaluateCombo(plainAIncludes, plainAExcludes, heldCtrlShiftA.Contains));

        // Holding only A triggers A-without-modifiers, does NOT trigger Ctrl+Shift+A
        var heldOnlyA = new HashSet<short> { 97 };
        Assert.IsFalse(InControlKeyComboEvaluator.EvaluateCombo(ctrlShiftAIncludes, Array.Empty<int>(), heldOnlyA.Contains));
        Assert.IsTrue(InControlKeyComboEvaluator.EvaluateCombo(plainAIncludes, plainAExcludes, heldOnlyA.Contains));

        // Holding LeftControl + A (missing Shift) triggers neither
        var heldCtrlA = new HashSet<short> { 306, 97 };
        Assert.IsFalse(InControlKeyComboEvaluator.EvaluateCombo(ctrlShiftAIncludes, Array.Empty<int>(), heldCtrlA.Contains));
        Assert.IsFalse(InControlKeyComboEvaluator.EvaluateCombo(plainAIncludes, plainAExcludes, heldCtrlA.Contains));
    }

    [TestMethod]
    public void NullArgumentsThrowArgumentNullException()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            InControlKeyComboEvaluator.IsInControlKeyHeld(KeyA, null!));
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            InControlKeyComboEvaluator.EvaluateCombo(null!, Array.Empty<int>(), _ => false));
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            InControlKeyComboEvaluator.EvaluateCombo(new[] { KeyA }, null!, _ => false));
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            InControlKeyComboEvaluator.EvaluateCombo(new[] { KeyA }, Array.Empty<int>(), null!));
    }
}
