namespace JunoSecondScreen.Util
{
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text;

    /// <summary>
    /// A small append-only JSON writer. Telemetry is serialized every frame, so this
    /// avoids the garbage a reflection-based serializer would produce.
    /// </summary>
    internal sealed class JsonWriter
    {
        private readonly StringBuilder _builder;
        private readonly Stack<bool> _firstInScope = new Stack<bool>();

        public JsonWriter(int capacity = 4096)
        {
            _builder = new StringBuilder(capacity);
        }

        /// <summary>
        /// Clears the buffer so the writer can be reused for the next frame.
        /// </summary>
        public void Reset()
        {
            _builder.Length = 0;
            _firstInScope.Clear();
        }

        public void StartObject()
        {
            Separate();
            _builder.Append('{');
            _firstInScope.Push(true);
        }

        public void StartObject(string key)
        {
            WriteKey(key);
            _builder.Append('{');
            _firstInScope.Push(true);
        }

        public void EndObject()
        {
            _builder.Append('}');
            _firstInScope.Pop();
        }

        public void StartArray(string key)
        {
            WriteKey(key);
            _builder.Append('[');
            _firstInScope.Push(true);
        }

        public void EndArray()
        {
            _builder.Append(']');
            _firstInScope.Pop();
        }

        public void Prop(string key, string value)
        {
            WriteKey(key);
            WriteString(value);
        }

        public void Prop(string key, bool value)
        {
            WriteKey(key);
            _builder.Append(value ? "true" : "false");
        }

        public void Prop(string key, double value)
        {
            WriteKey(key);
            WriteNumber(value);
        }

        public void Prop(string key, int value)
        {
            WriteKey(key);
            _builder.Append(value.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Writes a three component vector as a JSON array, the form the console's
        /// navball maths expects.
        /// </summary>
        public void PropVector(string key, double x, double y, double z)
        {
            WriteKey(key);
            _builder.Append('[');
            WriteNumber(x);
            _builder.Append(',');
            WriteNumber(y);
            _builder.Append(',');
            WriteNumber(z);
            _builder.Append(']');
        }

        public void PropNull(string key)
        {
            WriteKey(key);
            _builder.Append("null");
        }

        public override string ToString()
        {
            return _builder.ToString();
        }

        private void WriteKey(string key)
        {
            Separate();
            WriteString(key);
            _builder.Append(':');
        }

        private void Separate()
        {
            if (_firstInScope.Count == 0)
            {
                return;
            }

            if (_firstInScope.Peek())
            {
                _firstInScope.Pop();
                _firstInScope.Push(false);
            }
            else
            {
                _builder.Append(',');
            }
        }

        private void WriteNumber(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                _builder.Append("null");
                return;
            }

            _builder.Append(value.ToString("0.######", CultureInfo.InvariantCulture));
        }

        private void WriteString(string value)
        {
            if (value == null)
            {
                _builder.Append("null");
                return;
            }

            _builder.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '"': _builder.Append("\\\""); break;
                    case '\\': _builder.Append("\\\\"); break;
                    case '\n': _builder.Append("\\n"); break;
                    case '\r': _builder.Append("\\r"); break;
                    case '\t': _builder.Append("\\t"); break;
                    default:
                        if (c < ' ' || c > '~')
                        {
                            _builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            _builder.Append(c);
                        }

                        break;
                }
            }

            _builder.Append('"');
        }
    }
}
