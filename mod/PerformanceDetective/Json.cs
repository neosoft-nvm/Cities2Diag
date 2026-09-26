using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PerformanceDetective
{
    /// <summary>Minimal JSON writer (no JSON library is available to mods on .NET Framework 4.8).</summary>
    internal sealed class Json
    {
        private readonly StringBuilder _sb = new StringBuilder();
        private readonly Stack<bool> _first = new Stack<bool>();
        private bool _afterName;

        public Json BeginObject() { Separator(); _sb.Append('{'); _first.Push(true); return this; }
        public Json EndObject() { _first.Pop(); _sb.Append('}'); return this; }
        public Json BeginArray() { Separator(); _sb.Append('['); _first.Push(true); return this; }
        public Json EndArray() { _first.Pop(); _sb.Append(']'); return this; }

        public Json Name(string name)
        {
            Separator();
            Str(name);
            _sb.Append(':');
            _afterName = true;
            return this;
        }

        public Json Value(string v) { Separator(); if (v == null) _sb.Append("null"); else Str(v); return this; }
        public Json Value(bool v) { Separator(); _sb.Append(v ? "true" : "false"); return this; }
        public Json Value(long v) { Separator(); _sb.Append(v.ToString(CultureInfo.InvariantCulture)); return this; }

        public Json Value(double v, int decimals = 3)
        {
            Separator();
            if (double.IsNaN(v) || double.IsInfinity(v)) _sb.Append("null");
            else _sb.Append(System.Math.Round(v, decimals).ToString("R", CultureInfo.InvariantCulture));
            return this;
        }

        public Json Prop(string name, string v) => Name(name).Value(v);
        public Json Prop(string name, bool v) => Name(name).Value(v);
        public Json Prop(string name, long v) => Name(name).Value(v);
        public Json Prop(string name, double v, int decimals = 3) => Name(name).Value(v, decimals);

        private void Separator()
        {
            if (_afterName) { _afterName = false; return; }
            if (_first.Count == 0) return;
            if (_first.Peek()) { _first.Pop(); _first.Push(false); }
            else _sb.Append(',');
        }

        private void Str(string s)
        {
            _sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': _sb.Append("\\\""); break;
                    case '\\': _sb.Append("\\\\"); break;
                    case '\n': _sb.Append("\\n"); break;
                    case '\r': _sb.Append("\\r"); break;
                    case '\t': _sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) _sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else _sb.Append(c);
                        break;
                }
            }
            _sb.Append('"');
        }

        public override string ToString() => _sb.ToString();
    }
}
