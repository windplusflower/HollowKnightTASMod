using System;
using System.Collections.Generic;
using System.Globalization;

namespace HollowKnightTAS.Core.Inspector
{
    /// <summary>Small, bounded, read-only expression language. Field access stays in InfoWatchQuery.</summary>
    public sealed class InfoWatchExpression
    {
        public const int MaximumLength = 512;
        public static IReadOnlyList<string> SnapshotFields { get; } = Array.AsReadOnly(new[]
        {
            "frame", "nativeFrame", "room", "rt", "gt", "tft", "rng", "rngRolls", "rngDelta",
            "x", "y", "vx", "vy", "dash", "shade", "attack",
            "health", "maxHealth", "soul", "reserveSoul", "grounded", "facingRight", "jumping", "dashing"
        });
        private delegate object? Node(Func<InfoWatchQuery, object?> read, Func<string, object?> snapshot);
        private readonly Node node;
        private InfoWatchExpression(Node node) { this.node = node; }
        public static InfoWatchExpression Parse(string expression)
        {
            if (string.IsNullOrWhiteSpace(expression) || expression.Length > MaximumLength)
                throw new FormatException("Enter an expression (maximum 512 characters).");
            var parser = new Parser(expression);
            var result = parser.Conditional();
            if (!parser.End) throw new FormatException("Unexpected token at position " + parser.Offset + ".");
            return new InfoWatchExpression(result);
        }
        public object? Evaluate(Func<InfoWatchQuery, object?> read, Func<string, object?> snapshot)
            => Check(node(read, snapshot));
        private static object Check(object? value)
        {
            if (value == null) throw new InvalidOperationException("Value is unavailable.");
            if (value is string text && text.Length > 2048) throw new InvalidOperationException("Expression text is too long.");
            if (value is double d && (double.IsNaN(d) || double.IsInfinity(d))
                || value is float f && (float.IsNaN(f) || float.IsInfinity(f)))
                throw new InvalidOperationException("Expression result is not finite.");
            return value;
        }
        private static double Number(object? value)
        {
            Check(value);
            if (!(value is byte || value is sbyte || value is short || value is ushort || value is int || value is uint
                || value is long || value is ulong || value is float || value is double || value is decimal))
                throw new InvalidOperationException("This operator requires numbers.");
            return Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }
        private static bool Boolean(object? value) => value is bool b ? b : throw new InvalidOperationException("This operator requires booleans.");
        private static object Binary(string op, object? left, object? right)
        {
            Check(left); Check(right);
            if (op == "+" && (left is string || right is string))
                return Check(Convert.ToString(left, CultureInfo.InvariantCulture) + Convert.ToString(right, CultureInfo.InvariantCulture));
            if (op == "==" || op == "!=")
            {
                var equal = left is string || right is string || left is bool || right is bool
                    ? Equals(left, right) : Number(left) == Number(right);
                return op == "==" ? equal : !equal;
            }
            var a = Number(left); var b = Number(right);
            switch (op)
            {
                case "+": return Check(a + b);
                case "-": return Check(a - b);
                case "*": return Check(a * b);
                case "/": return b == 0 ? throw new InvalidOperationException("Division by zero.") : Check(a / b);
                case "%": return b == 0 ? throw new InvalidOperationException("Division by zero.") : Check(a % b);
                case "<": return a < b;
                case "<=": return a <= b;
                case ">": return a > b;
                case ">=": return a >= b;
                default: throw new InvalidOperationException("Unknown operator.");
            }
        }
        private sealed class Parser
        {
            private readonly string text;
            private int offset, depth;
            public Parser(string text) { this.text = text; }
            public int Offset => offset;
            public bool End { get { Space(); return offset == text.Length; } }
            private void Space() { while (offset < text.Length && char.IsWhiteSpace(text[offset])) offset++; }
            private bool Take(string token)
            {
                Space();
                if (offset + token.Length > text.Length || string.CompareOrdinal(text, offset, token, 0, token.Length) != 0) return false;
                offset += token.Length; return true;
            }
            private void Expect(string token) { if (!Take(token)) throw new FormatException("Expected '" + token + "'."); }
            private void Enter() { if (++depth > 32) throw new FormatException("Expression nesting exceeds 32 levels."); }
            public Node Conditional()
            {
                Enter();
                try
                {
                    var condition = Or();
                    if (!Take("?")) return condition;
                    var yes = Conditional(); Expect(":"); var no = Conditional();
                    return (r, s) => Boolean(condition(r, s)) ? yes(r, s) : no(r, s);
                }
                finally { depth--; }
            }
            private Node Or()
            {
                var result = And();
                while (Take("||")) { var left = result; var right = And(); result = (r, s) => Boolean(left(r, s)) || Boolean(right(r, s)); }
                return result;
            }
            private Node And()
            {
                var result = Equality();
                while (Take("&&")) { var left = result; var right = Equality(); result = (r, s) => Boolean(left(r, s)) && Boolean(right(r, s)); }
                return result;
            }
            private Node Equality() => Chain(Compare, new[] { "==", "!=" });
            private Node Compare() => Chain(Add, new[] { "<=", ">=", "<", ">" });
            private Node Add() => Chain(Multiply, new[] { "+", "-" });
            private Node Multiply() => Chain(Unary, new[] { "*", "/", "%" });
            private Node Chain(Func<Node> next, string[] operators)
            {
                var result = next();
                while (true)
                {
                    string? found = null;
                    foreach (var op in operators) if (Take(op)) { found = op; break; }
                    if (found == null) return result;
                    var left = result; var right = next(); var operation = found;
                    result = (r, s) => Binary(operation, left(r, s), right(r, s));
                }
            }
            private Node Unary()
            {
                Enter();
                try
                {
                    if (Take("!")) { var operand = Unary(); return (r, s) => !Boolean(operand(r, s)); }
                    if (Take("-")) { var operand = Unary(); return (r, s) => -Number(operand(r, s)); }
                    if (Take("+")) { var operand = Unary(); return (r, s) => Number(operand(r, s)); }
                    return Atom();
                }
                finally { depth--; }
            }
            private Node Atom()
            {
                Space();
                if (Take("(")) { var nested = Conditional(); Expect(")"); return nested; }
                if (offset == text.Length) throw new FormatException("Expected a value.");
                if (text[offset] == '"') { var literal = Quoted(); return (r, s) => literal; }
                var start = offset;
                if (char.IsDigit(text[offset]) || text[offset] == '.')
                {
                    while (offset < text.Length && (char.IsDigit(text[offset]) || text[offset] == '.')) offset++;
                    if (offset < text.Length && (text[offset] == 'e' || text[offset] == 'E'))
                    {
                        offset++;
                        if (offset < text.Length && (text[offset] == '+' || text[offset] == '-')) offset++;
                        while (offset < text.Length && char.IsDigit(text[offset])) offset++;
                    }
                    if (!double.TryParse(text.Substring(start, offset - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                        || double.IsInfinity(number) || double.IsNaN(number)) throw new FormatException("Invalid number.");
                    return (r, s) => number;
                }
                var name = Identifier();
                if (name == "true" || name == "false") return (r, s) => name == "true";
                if (name == "component" || name == "fsm")
                {
                    Expect("("); Quoted(); Expect(","); Quoted();
                    if (name == "fsm") { Expect(","); Quoted(); }
                    Expect(")");
                }
                var hasPath = false;
                while (Take("."))
                {
                    hasPath = true; Identifier();
                    if (Take("["))
                    {
                        Space();
                        while (offset < text.Length && char.IsDigit(text[offset])) offset++;
                        Expect("]");
                    }
                }
                if (!hasPath && name != "component" && name != "fsm")
                {
                    if (!System.Linq.Enumerable.Contains(SnapshotFields, name)) throw new FormatException("Unknown snapshot field: " + name);
                    return (r, s) => Check(s(name));
                }
                var query = InfoWatchQuery.Parse(text.Substring(start, offset - start).Trim());
                return (r, s) => Check(r(query));
            }
            private string Identifier()
            {
                Space(); var start = offset;
                if (offset == text.Length || !(char.IsLetter(text[offset]) || text[offset] == '_')) throw new FormatException("Expected a field name.");
                while (offset < text.Length && (char.IsLetterOrDigit(text[offset]) || text[offset] == '_')) offset++;
                return text.Substring(start, offset - start);
            }
            private string Quoted()
            {
                Expect("\""); var result = new System.Text.StringBuilder();
                while (offset < text.Length)
                {
                    var c = text[offset++];
                    if (c == '"') return result.ToString();
                    if (c == '\\')
                    {
                        if (offset == text.Length) break;
                        c = text[offset++];
                        if (c != '"' && c != '\\') throw new FormatException("Only escaped quotes and backslashes are supported.");
                    }
                    if (char.IsControl(c)) throw new FormatException("Control characters are not supported.");
                    result.Append(c);
                }
                throw new FormatException("Unterminated string.");
            }
        }
    }
}
